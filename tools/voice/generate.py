"""TamrielMerge voice generation: speaks converted dialogue lines in Skyrim's own voices.

Reads the job list written by `TamrielMerge voice-plan --out jobs.json` (voice type, text, target .fuz path) and,
for each voice type, clones the voice from that type's vanilla recordings (refs/<voice type>/*.wav, made by
`TamrielMerge voice-refs`) with XTTS-v2, writing one 44.1 kHz mono 16-bit .wav per distinct (voice, text) into
--wavs, named by a hash of the two. Already generated lines are skipped, so it can be stopped and resumed.
`TamrielMerge voice-pack-all` then turns the .wavs into .fuz files at the paths Skyrim reads.

Usage: python generate.py jobs.json --refs refs --wavs wavs [--voices A,B] [--limit N] [--fp16] [--shard I/N]
                          [--claim] [--device cuda|cpu] [--threads N] [--reverse] [--batch N]

--fp16 runs the model under half-precision autocast (faster on RTX tensor cores); --shard I/N speaks only the lines
whose hash falls in shard I of N, so N processes can share the GPU (one at batch size 1 leaves it partly idle).
--claim lets any number of processes share all the lines instead: each claims a line with a lock file
(wavs/<key>.wav.lock) before speaking it and skips lines claimed by another, so fast and slow workers (GPU and CPU)
each do as many as they can (user, 2026-10-09: "the cpu isnt being used"). --device cpu --threads N speaks on the
CPU (the GPU workers keep about one core each busy, the rest are free); --reverse takes the queue from its end.
--batch N speaks up to N lines of one voice and the same length in tokens together (user, 2026-10-09: "it shouldnt
take days its too slow"): XTTS writes its sound codes a step at a time, and one line a step left the graphics card
three-quarters idle while its worker's CPU core was busy; a batch's lines share each step. Same model and sampling as
one at a time (each line still gets its own latents and decoding); same-length lines need no padding.
A job's "SpeakAs" ("voice type[+effect]") is the voice it is spoken in when its speaker's own voice type is a creature's
(user, 2026-10-10: "dagoth is a ashen and vivec and almexia are gods from morrowind"): +god puts the gods' lines in a
great hall (reverb) with a faint lower voice beneath; +ash pitches the Sixth House's ashen down and roughens them.
"""
import argparse, hashlib, json, os, sys, time
from collections import defaultdict

os.environ.setdefault("COQUI_TOS_AGREED", "1")


def key(voice: str, text: str) -> str:
    return hashlib.sha1(f"{voice.lower()}\n{text}".encode("utf-8")).hexdigest()[:20]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("jobs")
    ap.add_argument("--refs", default="refs")
    ap.add_argument("--wavs", default="wavs")
    ap.add_argument("--voices", default="")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--fp16", action="store_true")
    ap.add_argument("--shard", default="0/1")
    ap.add_argument("--claim", action="store_true")
    ap.add_argument("--device", default="")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--reverse", action="store_true")
    ap.add_argument("--batch", type=int, default=1)
    args = ap.parse_args()

    jobs = json.load(open(args.jobs, encoding="utf-8"))
    wanted = {v.lower() for v in args.voices.split(",") if v}
    by_voice = defaultdict(dict)   # voice -> {key: text}
    priority = {}                  # key -> fewest speakers of the line (named speakers first)
    for j in sorted(jobs, key=lambda j: j.get("Speakers", 0)):
        v = j.get("SpeakAs") or j["VoiceType"]
        if v.startswith("recording:"):
            continue   # spoken with the game's own recording (decoded by the voice generator), not generated
        if wanted and v.lower() not in wanted:
            continue
        k = key(v, j["Text"])
        by_voice[v][k] = j["Text"]
        priority[k] = min(priority.get(k, 1 << 30), j.get("Speakers", 0))
    total = sum(len(t) for t in by_voice.values())
    os.makedirs(args.wavs, exist_ok=True)
    shard, shards = (int(x) for x in args.shard.split("/"))
    todo = {v: {k: t for k, t in texts.items() if int(k[:8], 16) % shards == shard and not os.path.exists(os.path.join(args.wavs, k + ".wav"))}
            for v, texts in by_voice.items()}
    remaining = sum(len(t) for t in todo.values())
    print(f"{total} distinct lines in {len(by_voice)} voices, {remaining} still to speak", flush=True)
    if remaining == 0:
        return

    import torch, torchaudio
    from TTS.api import TTS

    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    if device == "cpu" and args.threads:
        torch.set_num_threads(args.threads)
    tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to(device)
    model = tts.synthesizer.tts_model
    done, start = 0, time.time()
    # Lines in order of how few people say them (one named speaker first, generic lines last), across voices: the
    # voice's conditioning latents are computed once per voice and cached.
    queue = sorted(((priority[k], v, k, t) for v, texts in todo.items() for k, t in texts.items()), key=lambda x: (x[0], x[1]))
    if args.reverse:
        queue.reverse()
    # Batches: lines of one voice and one length in tokens, in queue order (a batch goes where its first line is).
    if args.batch > 1:
        groups, order = defaultdict(list), []
        for item in queue:
            g = (item[1], len(model.tokenizer.encode(item[3].strip().lower(), lang="en")))
            if g not in groups:
                order.append(g)
            groups[g].append(item)
        batches = [groups[g][i:i + args.batch] for g in order for i in range(0, len(groups[g]), args.batch)]
        print(f"  {len(queue)} lines in {len(batches)} batches of up to {args.batch}", flush=True)
    else:
        batches = [[item] for item in queue]
    if args.claim:
        # Locks left by a stopped worker (older than 30 minutes) are cleared.
        for f in os.listdir(args.wavs):
            if f.endswith(".lock") and time.time() - os.path.getmtime(os.path.join(args.wavs, f)) > 1800:
                try:
                    os.remove(os.path.join(args.wavs, f))
                except OSError:
                    pass
    latents = {}

    def cool_down(limit=78):
        """Waits while the GPU is above limit C (user, 2026-10-08: it overheated)."""
        import subprocess
        while True:
            try:
                t = int(subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                                       capture_output=True, text=True, timeout=10).stdout.split()[0])
            except Exception:
                return
            if t <= limit:
                return
            print(f"  GPU at {t} C, waiting to cool below {limit} C", flush=True)
            time.sleep(20)

    def speak_batch(texts, latent, speaker):
        """Several lines of one voice and one token length spoken together: the sound codes written for all at once,
        then each line's latents and audio as model.inference makes them."""
        toks = torch.stack([torch.IntTensor(model.tokenizer.encode(t.strip().lower(), lang="en")) for t in texts]).to(model.device)
        b = toks.shape[0]
        with torch.no_grad():
            codes = model.gpt.generate(cond_latents=latent.expand(b, -1, -1), text_inputs=toks, do_sample=True, top_p=0.85,
                                       top_k=50, temperature=0.65, num_return_sequences=1, num_beams=1, length_penalty=1.0,
                                       repetition_penalty=5.0, output_attentions=False)
            wavs = []
            for i in range(b):
                c = codes[i:i + 1]
                stops = (c[0] == model.gpt.stop_audio_token).nonzero()
                if len(stops):
                    c = c[:, :int(stops[0]) + 1]
                expected = torch.tensor([c.shape[-1] * model.gpt.code_stride_len], device=toks.device)
                lat = model.gpt(toks[i:i + 1], torch.tensor([toks.shape[-1]], device=toks.device), c, expected,
                                cond_latents=latent, return_attentions=False, return_latent=True)
                wavs.append(model.hifigan_decoder(lat, g=speaker).cpu().squeeze())
        return wavs

    def effect(wav, name):
        """The stand-in voices' treatment, at the model's 24 kHz."""
        sr = 24000
        if name == "ash":
            low = torchaudio.functional.pitch_shift(wav, sr, n_steps=-3)
            return 0.75 * low + 0.25 * torch.tanh(low * 4) / 2           # deeper, with a rasp
        if name == "god":
            g = torch.Generator().manual_seed(7)
            t = torch.arange(int(sr * 1.4)) / sr
            ir = torch.randn(len(t), generator=g) * torch.exp(-t * 4.0)   # a great hall's tail
            ir[0] = 0
            hall = torchaudio.functional.fftconvolve(wav, (ir / ir.abs().sum() * 6).reshape(1, -1))[:, :wav.shape[1] + sr]
            under = torchaudio.functional.pitch_shift(wav, sr, n_steps=-7)   # a fainter voice a fifth below
            dry = torch.nn.functional.pad(wav + 0.22 * under, (0, hall.shape[1] - wav.shape[1]))
            return dry + 0.35 * hall
        return wav

    def save(k, voice, audio):
        path = os.path.join(args.wavs, k + ".wav")
        wav = torch.as_tensor(audio).float().cpu().reshape(1, -1)
        if not torch.isfinite(wav).all():
            print(f"  {voice}: non-finite audio for {k}, skipped", flush=True)
            return False
        if "+" in voice:
            wav = effect(wav, voice.split("+", 1)[1])
        wav = torchaudio.functional.resample(wav, 24000, 44100)
        peak = wav.abs().max().item()
        if peak > 0:
            wav = wav * min(1.0, 0.95 / peak)
        torchaudio.save(path + ".tmp.wav", wav, 44100, encoding="PCM_S", bits_per_sample=16)
        os.replace(path + ".tmp.wav", path)
        return True

    for n, batch in enumerate(batches):
        voice = batch[0][1]
        # This worker's lines of the batch: not spoken yet and, with --claim, claimed here.
        mine = []
        for _, _, k, text in batch:
            path = os.path.join(args.wavs, k + ".wav")
            if os.path.exists(path):
                continue
            if args.claim:
                try:
                    os.close(os.open(path + ".lock", os.O_CREAT | os.O_EXCL | os.O_WRONLY))
                except FileExistsError:
                    continue
            mine.append((k, text))
        if not mine:
            continue
        if device == "cuda" and n % 10 == 0:
            cool_down()
        if voice not in latents:
            ref_dir = os.path.join(args.refs, voice.split("+")[0])
            refs = [os.path.join(ref_dir, f) for f in sorted(os.listdir(ref_dir)) if f.endswith(".wav")] if os.path.isdir(ref_dir) else []
            if not refs:
                print(f"  {voice}: no reference recordings, skipped", flush=True)
                latents[voice] = None
            else:
                latents[voice] = model.get_conditioning_latents(audio_path=refs, gpt_cond_len=30, max_ref_length=60)
        if latents[voice] is not None:
            latent, speaker = latents[voice]
            with torch.autocast("cuda", dtype=torch.float16, enabled=args.fp16 and device == "cuda"):
                if len(mine) == 1:
                    audio = [model.inference(mine[0][1], "en", latent, speaker, temperature=0.65, repetition_penalty=5.0,
                                             enable_text_splitting=False)["wav"]]
                else:
                    audio = speak_batch([t for _, t in mine], latent, speaker)
            for (k, _), a in zip(mine, audio):
                done += save(k, voice, a)
        if args.claim:
            for k, _ in mine:
                try:
                    os.remove(os.path.join(args.wavs, k + ".wav.lock"))
                except OSError:
                    pass
        if done and (done // 25) != ((done - len(mine)) // 25):
            rate = done / (time.time() - start)
            print(f"  {done}/{remaining} lines ({rate:.2f}/s, about {(remaining - done) / rate / 3600:.1f} h left)", flush=True)
        if args.limit and done >= args.limit:
            print(f"limit reached: {done} lines", flush=True)
            return
    print(f"done: {done} lines in {(time.time() - start) / 60:.1f} min ({(time.time() - start) / max(done, 1):.1f} s a line)", flush=True)


if __name__ == "__main__":
    main()
