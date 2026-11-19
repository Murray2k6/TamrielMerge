"""TamrielMerge voice generation: speaks converted dialogue lines in Skyrim's own voices.

Reads the job list written by `TamrielMerge voice-plan --out jobs.json` (voice type, text, target .fuz path) and,
for each voice type, clones the voice from that type's vanilla recordings (refs/<voice type>/*.wav, made by
`TamrielMerge voice-refs`) with XTTS-v2, writing one 44.1 kHz mono 16-bit .wav per distinct (voice, text) into
--wavs, named by a hash of the two. Already generated lines are skipped, so it can be stopped and resumed.
`TamrielMerge voice-pack-all` then turns the .wavs into .fuz files at the paths Skyrim reads.

Usage: python generate.py jobs.json --refs refs --wavs wavs [--voices A,B] [--limit N] [--fp16] [--shard I/N]
                          [--claim] [--device cuda|cpu] [--threads N] [--reverse] [--batch N] [--half] [--savers N]
                          [--share] [--render N]

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
--half keeps the GPT's transformer in half precision, about half a worker's graphics memory, so several share a card.
--savers N saves lines on N threads while the worker goes on to its next batch.
--share writes a sentence's sound codes once for all the male voices saying it, and once for all the female ones, then
makes each voice's speech from them with that voice's own latents and speaker (user, 2026-11-01: "18 hours is still way
too long"; "all the races are different and all sound different": every voice stays). Writing the codes is 83% of a line
(measured on the CPU: 15.0 s, against 0.8 and 2.2 for the two steps after it), and 129,558 lines are 26,625 sentences,
36,892 by sentence and sex. A voice with effects (+god, +ash) is its plain voice's speech treated. A sentence only one
voice says is written in that voice, as before. --render N makes up to N voices of a sentence at once: a sentence's
codes are one length, so they need no padding.
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
    ap.add_argument("--half", action="store_true")
    ap.add_argument("--savers", type=int, default=2)
    ap.add_argument("--share", action="store_true")
    ap.add_argument("--render", type=int, default=4)
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
    if args.half and device == "cuda":
        # The GPT's transformer that writes the sound codes kept in half precision (user, 2026-10-24: "it needs to use
        # both gpu and ram"): its weights are most of a worker's graphics memory, so more workers share the card. The
        # voice's conditioning (its encoder and resampler) and the decoder that makes the sound stay full precision.
        model.gpt.gpt.half()
    print(f"  model on {device}{' (GPT in half precision)' if args.half and device == 'cuda' else ''}: "
          f"{torch.cuda.memory_allocated() / 2 ** 20:.0f} MB of graphics memory" if device == "cuda" else f"  model on {device}", flush=True)
    done, start = 0, time.time()
    # Lines in order of how few people say them (one named speaker first, generic lines last), across voices: the
    # voice's conditioning latents are computed once per voice and cached.
    queue = sorted(((priority[k], v, k, t) for v, texts in todo.items() for k, t in texts.items()), key=lambda x: (x[0], x[1]))
    if args.reverse:
        queue.reverse()
    # Batches: lines of one voice and one length in tokens, in queue order (a batch goes where its first line is).
    if args.share:
        batches = []
    elif args.batch > 1:
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
        """Waits while the GPU is above limit C (user, 2026-10-08: it overheated). This worker's own card: TM_GPU names it
        (the converter gives each worker one card of several)."""
        import subprocess
        gpu = os.environ.get("TM_GPU", "")
        while True:
            try:
                t = int(subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"] + ([f"--id={gpu}"] if gpu else []),
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

    import fx   # the fast pitch shift (fx.py: torchaudio's own took 6.2 s a line, measured 2026-11-11)

    def effect(wav, name):
        """The stand-in voices' treatment, at the model's 24 kHz."""
        sr = 24000
        if name == "ash":
            low = fx.shift(wav, sr, -3)
            return 0.75 * low + 0.25 * torch.tanh(low * 4) / 2           # deeper, with a rasp
        if name == "god":
            g = torch.Generator().manual_seed(7)
            t = torch.arange(int(sr * 1.4)) / sr
            ir = torch.randn(len(t), generator=g) * torch.exp(-t * 4.0)   # a great hall's tail
            ir[0] = 0
            hall = torchaudio.functional.fftconvolve(wav, (ir / ir.abs().sum() * 6).reshape(1, -1))[:, :wav.shape[1] + sr]
            under = fx.shift(wav, sr, -7)   # a fainter voice a fifth below
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

    from concurrent.futures import ThreadPoolExecutor
    savers = ThreadPoolExecutor(max_workers=max(1, args.savers))
    pending = []

    def release(k):
        """A line's claim let go, once it is saved (or can't be): before that another worker would speak it again."""
        if args.claim:
            try:
                os.remove(os.path.join(args.wavs, k + ".wav.lock"))
            except OSError:
                pass

    def save_released(k, voice, audio):
        try:
            return save(k, voice, audio)
        finally:
            release(k)

    def collect(wait=False):
        """The saves finished so far (all of them, waiting, when wait): how many lines were saved."""
        saved = 0
        for f in list(pending):
            if wait or f.done():
                saved += bool(f.result())
                pending.remove(f)
        return saved

    def voice_latents(voice):
        """A voice's latents and speaker (its plain voice's, without effects), read once; None without recordings."""
        plain = voice.split("+")[0]
        if plain not in latents:
            ref_dir = os.path.join(args.refs, plain)
            refs = [os.path.join(ref_dir, f) for f in sorted(os.listdir(ref_dir)) if f.endswith(".wav")] if os.path.isdir(ref_dir) else []
            if refs:
                # Full precision (see the same call below).
                latents[plain] = model.get_conditioning_latents(audio_path=refs, gpt_cond_len=30, max_ref_length=60)
            else:
                print(f"  {voice}: no reference recordings, skipped", flush=True)
                latents[plain] = None
        return latents[plain]

    def sex_of(voice):
        plain = voice.split("+")[0].lower()
        return "female" if plain.startswith("female") else "male" if plain.startswith("male") else plain

    # The voices that write a sentence's codes for the others of their sex: Skyrim's plainest.
    writers = {"male": "maleeventoned", "female": "femaleeventoned"}

    def speak_shared():
        """--share: the lines to speak by sentence and sex, a batch of sentences at a time. One claim a sentence, let go
        when all its voices are saved."""
        import threading
        sentences = defaultdict(list)
        for _, voice, k, text in queue:
            sentences[(sex_of(voice), text)].append((voice, k))
        buckets, order = defaultdict(list), []
        for (sex, text), lines in sentences.items():
            g = (sex, len(model.tokenizer.encode(text.strip().lower(), lang="en")))
            if g not in buckets:
                order.append(g)
            buckets[g].append((sex, text, lines))
        batches = [buckets[g][i:i + args.batch] for g in order for i in range(0, len(buckets[g]), args.batch)]
        print(f"  {len(queue)} lines of {len(sentences)} sentences by sex, in {len(batches)} batches of up to {args.batch}", flush=True)
        saving, gate, shared_done = {}, threading.Lock(), 0

        def claim_path(sex, text):
            return os.path.join(args.wavs, key(sex, text) + ".shared.lock")

        def release_claim(path):
            if args.claim:
                try:
                    os.remove(path)
                except OSError:
                    pass

        def save_shared(k, voice, audio, path):
            try:
                return save(k, voice, audio)
            finally:
                with gate:
                    saving[path] -= 1
                    last = saving[path] == 0
                if last:
                    release_claim(path)

        for n, batch in enumerate(batches):
            mine = []
            for sex, text, lines in batch:
                todo_here = [(v, k) for v, k in lines if not os.path.exists(os.path.join(args.wavs, k + ".wav"))]
                if not todo_here:
                    continue
                path = claim_path(sex, text)
                if args.claim:
                    try:
                        os.close(os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
                    except FileExistsError:
                        continue
                # By plain voice: each is made once, and saved as each of its lines (plain, +god, +ash).
                voices = defaultdict(list)
                for v, k in todo_here:
                    if voice_latents(v) is None:
                        continue
                    voices[v.split("+")[0]].append((v, k))
                if not voices:
                    release_claim(path)
                    continue
                by_lower = {v.lower(): v for v in voices}
                writer = by_lower.get(writers.get(sex, ""), sorted(voices)[0])
                mine.append((text, writer, voices, path))
            if not mine:
                continue
            if device == "cuda" and n % 10 == 0:
                cool_down()
            toks = torch.stack([torch.IntTensor(model.tokenizer.encode(t.strip().lower(), lang="en")) for t, _, _, _ in mine]).to(model.device)
            spoken = 0
            with torch.autocast("cuda", dtype=torch.float16, enabled=(args.fp16 or args.half) and device == "cuda"), torch.no_grad():
                cond = torch.cat([voice_latents(w)[0] for _, w, _, _ in mine])
                codes = model.gpt.generate(cond_latents=cond, text_inputs=toks, do_sample=True, top_p=0.85, top_k=50, temperature=0.65,
                                           num_return_sequences=1, num_beams=1, length_penalty=1.0, repetition_penalty=5.0, output_attentions=False)
                for i, (text, writer, voices, path) in enumerate(mine):
                    c = codes[i:i + 1]
                    stops = (c[0] == model.gpt.stop_audio_token).nonzero()
                    if len(stops):
                        c = c[:, :int(stops[0]) + 1]
                    names = list(voices)
                    with gate:
                        saving[path] = sum(len(voices[v]) for v in names)
                    for at in range(0, len(names), max(1, args.render)):
                        part = names[at:at + max(1, args.render)]
                        m = len(part)
                        lat = model.gpt(toks[i:i + 1].expand(m, -1), torch.full((m,), toks.shape[-1], device=toks.device), c.expand(m, -1),
                                        torch.full((m,), c.shape[-1] * model.gpt.code_stride_len, device=toks.device),
                                        cond_latents=torch.cat([voice_latents(v)[0] for v in part]), return_attentions=False, return_latent=True)
                        wavs = model.hifigan_decoder(lat, g=torch.cat([voice_latents(v)[1] for v in part])).float().cpu().reshape(m, -1)
                        for j, v in enumerate(part):
                            for line_voice, k in voices[v]:
                                pending.append(savers.submit(save_shared, k, line_voice, wavs[j].clone(), path))
                                spoken += 1
            nonlocal_done[0] += collect(wait=len(pending) > 8 * args.savers)
            count = nonlocal_done[0]
            if count and (count // 100) != ((count - spoken) // 100):
                rate = count / (time.time() - start)
                print(f"  {count}/{remaining} lines ({rate:.2f}/s, about {(remaining - count) / rate / 3600:.1f} h left)", flush=True)
            if args.limit and count >= args.limit:
                print(f"limit reached: {count} lines", flush=True)
                break
        nonlocal_done[0] += collect(wait=True)

    if args.share:
        nonlocal_done = [0]
        speak_shared()
        print(f"done: {nonlocal_done[0]} lines in {(time.time() - start) / 60:.1f} min ({(time.time() - start) / max(nonlocal_done[0], 1):.1f} s a line)", flush=True)
        return

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
                # Full precision (2026-10-24: under half-precision autocast the latents came out so that every next
                # sound code had probability 0, and sampling failed a CUDA assertion on the first batch).
                latents[voice] = model.get_conditioning_latents(audio_path=refs, gpt_cond_len=30, max_ref_length=60)
        if latents[voice] is not None:
            latent, speaker = latents[voice]
            with torch.autocast("cuda", dtype=torch.float16, enabled=(args.fp16 or args.half) and device == "cuda"):
                if len(mine) == 1:
                    audio = [model.inference(mine[0][1], "en", latent, speaker, temperature=0.65, repetition_penalty=5.0,
                                             enable_text_splitting=False)["wav"]]
                else:
                    audio = speak_batch([t for _, t in mine], latent, speaker)
            # Saved on other threads (effects, resampling, writing), while this one goes on to the next batch: the
            # graphics card waited through every save before.
            for (k, _), a in zip(mine, audio):
                pending.append(savers.submit(save_released, k, voice, torch.as_tensor(a).float().cpu()))
            done += collect(wait=len(pending) > 4 * args.savers)
        else:
            for k, _ in mine:
                release(k)
        if done and (done // 25) != ((done - len(mine)) // 25):
            rate = done / (time.time() - start)
            print(f"  {done}/{remaining} lines ({rate:.2f}/s, about {(remaining - done) / rate / 3600:.1f} h left)", flush=True)
        if args.limit and done >= args.limit:
            done += collect(wait=True)
            print(f"limit reached: {done} lines", flush=True)
            return
    done += collect(wait=True)
    print(f"done: {done} lines in {(time.time() - start) / 60:.1f} min ({(time.time() - start) / max(done, 1):.1f} s a line)", flush=True)


if __name__ == "__main__":
    main()
