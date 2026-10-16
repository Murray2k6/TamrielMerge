// The Tamriel menu (TamrielMerge, docs/progression.md). ActionScript 2 for Skyrim's Scaleform, compiled with FFDec
// (Skyrim.Ui.FlashMenus) and opened by TMProgression through SKSE's UI.OpenCustomMenu. Everything is drawn in code.
// Papyrus fills it: ClearFactions / AddFaction("game|name|rank|ranks|title") / ShowFactions, and ClearMoney /
// AddMoney("game|coin|amount|carried|story") / ShowMoney; it asks for that data when loaded (mod event TM_MenuReady) and
// reports closing (TM_MenuClose). Opened by a money changer or bank (TMMoneyChanger), it also gets OpenService("kind|
// title|fee"), BankState(...) and ServiceStatus(text), and sends each button as mod event TM_Service(action, amount).
// Travel (TMTravel, docs/travel.md: every converted world, Daggerfall's travel made in real time): it sends TM_Travel
// ("worlds"; "world:w"; "group:w:g"; "pick:w:g:t"; "opt:cautious|inn|ship|horse"; "speed" with 1-10; "go"; "continue")
// and gets ClearTravel / AddTravel("w|w|name|status", "g|w:g|name|towns" or "t|w:g:t|name|time") / ShowTravel(title) /
// TravelStatus(text) / TripInfo("name|kind|time|cost|gold|cautious|inn|ship|horse|horse here|speed|note|world|era"); each
// destination is labelled with its time's era and year ("3E 427").
// Skills (TMSkills): the build writes the skills and their perk trees into `built`; opening the tab sends TM_Skills
// ("state"; "take:<perk>") and Papyrus answers SkillState("level,xp,need;...#df level,progress;...#owned perks#perk
// points#message"). Passives (TMPassives): the web's nodes and links are in `built` too; the tab sends TM_Web ("state";
// "take:<node>"; "reset") and gets WebState("taken#free points#start#sign#gates open#message#level").

var built = null; // the build's skills and passive web (ProgressionBuilder.MenuData)
var root = this;
var W = 1280;
var H = 720;
var PAD = 60;
var factions = [];
var money = [];
var tab = 0;
var page = 0;
var PER_PAGE = 18;

function box(mc, x, y, w, h, colour, alpha) {
    mc.beginFill(colour, alpha);
    mc.moveTo(x, y); mc.lineTo(x + w, y); mc.lineTo(x + w, y + h); mc.lineTo(x, y + h); mc.lineTo(x, y);
    mc.endFill();
}

function text(parent, name, depth, x, y, w, h, size, colour, bold, align) {
    parent.createTextField(name, depth, x, y, w, h);
    var t = parent[name];
    var f = new TextFormat();
    f.font = bold ? "$EverywhereBoldFont" : "$EverywhereFont";
    f.size = size;
    f.color = colour;
    if (align != undefined) f.align = align;
    t.embedFonts = true;
    t.selectable = false;
    t.setNewTextFormat(f);
    return t;
}

function button(parent, name, depth, x, y, w, h, label, onPress) {
    var b = parent.createEmptyMovieClip(name, depth);
    box(b, 0, 0, w, h, 0x2A2A30, 90);
    b._x = x; b._y = y;
    var t = text(b, "label", 1, 0, 6, w, h - 6, 18, 0xE8E2D0, false, "center");
    t.text = label;
    b.onRelease = onPress;
    b.onRollOver = function() { this._alpha = 80; };
    b.onRollOut = function() { this._alpha = 100; };
    return b;
}

function close() {
    skse.SendModEvent("TM_MenuClose", "", 0);
    skse.CloseMenu("CustomMenu");
}

// ---- frame ----
var back = this.createEmptyMovieClip("back", 1);
box(back, 0, 0, W, H, 0x000000, 60);
box(back, PAD, PAD - 10, W - 2 * PAD, H - 2 * PAD + 20, 0x121216, 92);
var title = text(this, "title", 2, PAD + 20, PAD, 400, 40, 30, 0xF0E6C8, true);
title.text = "Tamriel";
var purse = text(this, "purse", 3, PAD + 20, PAD + 38, 380, 24, 16, 0xD9B76A, false);
purse.text = "";
var tabNames = ["Factions", "Coin", "Travel", "Skills", "Passives"];
for (var i = 0; i < tabNames.length; i++) {
    var b = button(this, "tab" + i, 10 + i, PAD + 330 + i * 160, PAD + 4, 150, 34, tabNames[i], null);
    b.index = i;
    b.onRelease = function() {
        tab = this.index; page = 0;
        if (tab == 2) { trip = null; askTravel("worlds", 0); }
        if (tab == 3) skse.SendModEvent("TM_Skills", "state", 0);
        if (tab == 4) skse.SendModEvent("TM_Web", "state", 0);
        draw();
    };
}
button(this, "closeButton", 20, W - PAD - 130, H - PAD - 34, 110, 34, "Close", function() { close(); });
button(this, "prevButton", 21, PAD + 20, H - PAD - 34, 110, 34, "< Previous", function() { if (page > 0) { page--; draw(); } });
button(this, "nextButton", 22, PAD + 140, H - PAD - 34, 110, 34, "Next >", function() { page++; draw(); });
// Skills: the buttons step through the skills; Passives has no pages.
root.prevButton.onRelease = function() { if (tab == 3) { if (skillPick > 0) skillPick--; draw(); } else if (page > 0) { page--; draw(); } };
root.nextButton.onRelease = function() { if (tab == 3) skillPick++; else page++; draw(); };
var body = this.createEmptyMovieClip("body", 5);

var keys = new Object();
keys.onKeyDown = function() { if (Key.getCode() == Key.ESCAPE) close(); };
Key.addListener(keys);

// ---- Papyrus ----
// Whether Papyrus has sent each list yet (until then the tab says it is reading them).
var factionsReady = false;
var moneyReady = false;
function ClearFactions(s) { factions = []; }
function AddFaction(line) { factions.push(line.split("|")); }
function ShowFactions(s) { factionsReady = true; if (tab == 0) draw(); }
function ClearMoney(s) { money = []; }
function AddMoney(line) { money.push(line.split("|")); }
function ShowMoney(s) {
    moneyReady = true;
    purse.text = "";
    for (var i = 0; i < money.length; i++)
        if (money[i][3] == "1") purse.text = "Carrying " + money[i][2] + " " + money[i][1] + " (" + money[i][0] + ")";
    if (tab == 1) draw();
}

// ---- tabs ----
function draw() {
    body.removeMovieClip();
    body = root.createEmptyMovieClip("body", 5);
    for (var i = 0; i < tabNames.length; i++) root["tab" + i]._alpha = i == tab ? 100 : 55;
    root.prevButton._visible = tab != 1 && tab != 4;
    root.nextButton._visible = tab != 1 && tab != 4;
    if (tab == 0) drawFactions();
    else if (tab == 1) drawMoney();
    else if (tab == 2) drawTravel();
    else if (tab == 3) drawSkills();
    else drawWeb();
}

// A tab's message when it has nothing to list: still reading, or what there is (or isn't) and why.
function drawNote(what) {
    var t = text(body, "note", 1, PAD + 20, PAD + 70, W - 2 * PAD - 40, 90, 20, 0xA8A090, false);
    t.wordWrap = true;
    t.multiline = true;
    t.text = what;
}

// Each converted game's coin and how much of it the player has; the one carried is the coin of the time they're in.
// With a money changer's or bank's service open, other purses can be changed and the bank's business done below.
function drawMoney() {
    if (!moneyReady) { drawNote("Counting your coin..."); return; }
    if (money.length == 0) { drawNote("You carry no coin of any time."); return; }
    var head = text(body, "note", 1, PAD + 20, PAD + 66, W - 2 * PAD - 40, 30, 16, 0x8C877C, false);
    head.text = "Each time keeps its own coin. You carry the coin of the time you are in; the rest waits in its purse.";
    for (var i = 0; i < money.length; i++) {
        var m = money[i];
        var y = PAD + 104 + i * 40;
        var carried = m[3] == "1";
        if (carried) box(body, PAD + 14, y - 4, W - 2 * PAD - 28, 36, 0x2A2A30, 80);
        var g = text(body, "g" + i, 100 + i * 4, PAD + 30, y + 2, 300, 32, 20, 0xD9B76A, true);
        g.text = m[0];
        var c = text(body, "c" + i, 101 + i * 4, PAD + 340, y + 2, 420, 32, 20, carried ? 0xF0E6C8 : 0xA8A090, false);
        c.text = m[2] + " " + m[1];
        if (service != null && !carried && Number(m[2]) > 0) {
            var x = button(body, "x" + i, 102 + i * 4, W - PAD - 200, y - 2, 170, 32, "Exchange", null);
            x.story = m[4];
            x.onRelease = function() { serve("exchange:" + this.story); };
        } else {
            var s = text(body, "s" + i, 103 + i * 4, W - PAD - 330, y + 4, 300, 32, 16, 0x8C877C, false, "right");
            s.text = carried ? "carried" : "in its purse";
        }
    }
    if (service != null) drawService(PAD + 104 + money.length * 40 + 6);
}

// ---- services: a money changer's or a bank's (TMMoneyChanger) ----
var service = null;
var bank = null;
var amount = 100;
var status = "";
function OpenService(line) {
    var p = line.split("|");
    service = { kind: p[0], title: p[1], fee: Number(p[2]) };
    if (service.kind != "bank") bank = null;
    tab = 1;
    draw();
}
function BankState(line) { bank = line.split("|"); if (tab == 1) draw(); }
function ServiceStatus(s) { status = s; if (tab == 1) draw(); }
function serve(action) { skse.SendModEvent("TM_Service", action, amount); }

function drawService(top) {
    box(body, PAD + 14, top, W - 2 * PAD - 28, 2, 0x5A5040, 100);
    var t = text(body, "svTitle", 200, PAD + 30, top + 8, 700, 30, 20, 0xF0E6C8, true);
    t.text = service.title + "  -  exchange fee " + service.fee + "%";
    // The amount every action uses.
    var a = text(body, "svAmount", 201, PAD + 30, top + 48, 260, 30, 18, 0xF0E6C8, false);
    a.text = "Amount: " + amount;
    var steps = [-1000, -100, -10, 10, 100, 1000];
    for (var i = 0; i < steps.length; i++) {
        var b = button(body, "step" + i, 210 + i, PAD + 300 + i * 92, top + 44, 84, 32, (steps[i] > 0 ? "+" : "") + steps[i], null);
        b.step = steps[i];
        b.onRelease = function() { amount += this.step; if (amount < 0) amount = 0; draw(); };
    }
    var y = top + 88;
    if (bank != null) {
        var loan = Number(bank[1]);
        var due = loan <= 0 ? "no loan" : "loan " + loan + (bank[3] == "1" ? " (overdue)" : " (due in " + bank[2] + " days)");
        var info = text(body, "svBank", 220, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xD9B76A, false);
        info.text = "Account " + bank[0] + "   |   " + due + "   |   letters of credit " + bank[4] + "   |   you may borrow up to " + bank[5] + (bank[6] == "1" ? " (refused: an old loan defaulted)" : "");
        var acts = [["Deposit", "deposit"], ["Withdraw", "withdraw"], ["Letter of credit", "letter"], ["Cash letters", "cash"], ["Borrow", "borrow"], ["Repay", "repay"]];
        for (var j = 0; j < acts.length; j++) {
            var ab = button(body, "act" + j, 230 + j, PAD + 30 + j * 182, y + 36, 172, 34, acts[j][0], null);
            ab.action = acts[j][1];
            ab.onRelease = function() { serve(this.action); };
        }
        y += 80;
    }
    var st = text(body, "svStatus", 240, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xA8A090, false);
    st.text = status;
}

// ---- travel: Daggerfall's, in every converted world (TMTravel) ----
var travel = [];
var travelTitle = "";
var travelStatus = "";
var trip = null;
var TRAVEL_ROWS = 12;
function ClearTravel(s) { travel = []; travelStatus = ""; trip = null; }
function AddTravel(line) { travel.push(line.split("|")); }
function ShowTravel(s) { travelTitle = s; page = 0; if (tab == 2) draw(); }
function TravelStatus(s) { travelStatus = s; if (tab == 2) draw(); }
function TripInfo(line) {
    var p = line.split("|");
    trip = { name: p[0], kind: p[1], time: p[2], cost: Number(p[3]), gold: Number(p[4]), cautious: p[5] == "1", inn: p[6] == "1",
             ship: p[7] == "1", horse: p[8] == "1", horseHere: p[9] == "1", speed: Number(p[10]), note: p[11],
             world: p[12], era: p[13] };
    if (tab == 2) draw();
}
function askTravel(what, n) { skse.SendModEvent("TM_Travel", what, n); }

// Worlds, then a world's regions, then a region's towns with the time on the road; a town opens its trip.
function drawTravel() {
    if (trip != null) { drawTrip(); return; }
    if (travel.length == 0) {
        var none = text(body, "none", 1, PAD + 20, PAD + 70, W - 2 * PAD - 40, 40, 20, 0xA8A090, false);
        none.text = travelStatus != "" ? travelStatus : "Travel is about the converted games' worlds.";
        return;
    }
    var kind = travel[0][0];
    var head = text(body, "head", 1, PAD + 20, PAD + 66, 760, 30, 20, 0xD9B76A, true);
    head.text = travelTitle + (kind == "w" ? "  -  choose a world" : kind == "g" ? "  -  choose a region" : "  -  choose a town");
    if (kind != "w") {
        var ids = travel[0][1].split(":");
        var backButton = button(body, "back", 2, W - PAD - 200, PAD + 62, 170, 32, kind == "g" ? "< Worlds" : "< Regions", null);
        backButton.world = ids[0];
        if (kind == "g") backButton.onRelease = function() { askTravel("worlds", 0); };
        else backButton.onRelease = function() { askTravel("world:" + this.world, 0); };
    }
    var per = TRAVEL_ROWS * 2;
    var pages = Math.ceil(travel.length / per);
    if (page >= pages) page = pages - 1;
    for (var n = 0; n < per && page * per + n < travel.length; n++) {
        var e = travel[page * per + n];
        var column = Math.floor(n / TRAVEL_ROWS);
        var x = PAD + 20 + column * ((W - 2 * PAD - 40) / 2);
        var y = PAD + 104 + (n % TRAVEL_ROWS) * 38;
        var label = e[2];
        if (kind == "w") label += "  (" + e[3] + ")";
        else if (kind == "g") label += "  (" + e[3] + " towns)";
        else if (e[3] != "") label += "  -  " + e[3];
        var b = button(body, "e" + n, 100 + n, x, y, (W - 2 * PAD - 60) / 2, 34, label, null);
        b.id = e[1];
        if (kind == "w") b.onRelease = function() { askTravel("world:" + this.id, 0); };
        else if (kind == "g") b.onRelease = function() { askTravel("group:" + this.id, 0); };
        else b.onRelease = function() { lastPick = this.id; askTravel("pick:" + this.id, 0); };
    }
    var p = text(body, "pageNo", 400, W / 2 - 100, H - PAD - 30, 200, 30, 16, 0x8C877C, false, "center");
    p.text = "page " + (page + 1) + " of " + pages;
    if (travelStatus != "") {
        var st = text(body, "status", 401, PAD + 20, H - PAD - 64, W - 2 * PAD - 300, 30, 16, 0xA8A090, false);
        st.text = travelStatus;
        var cont = button(body, "continue", 402, W - PAD - 270, H - PAD - 70, 250, 32, "Continue the journey", null);
        cont.onRelease = function() { skse.SendModEvent("TM_Travel", "continue", 0); close(); };
    }
}

// Daggerfall's trip: where, how long, what it costs, the options, the pace, and Begin.
var lastPick = "";
function drawTrip() {
    var t = trip;
    var head = text(body, "head", 1, PAD + 20, PAD + 66, 760, 34, 24, 0xD9B76A, true);
    head.text = "Travel to " + t.name + ", " + t.world + (t.era != "" ? "  -  " + t.era : "");
    var backButton = button(body, "back", 2, W - PAD - 200, PAD + 62, 170, 32, "< Towns", null);
    backButton.onRelease = function() {
        trip = null;
        var ids = lastPick.split(":");
        askTravel("group:" + ids[0] + ":" + ids[1], 0);
    };
    var y = PAD + 118;
    var info = text(body, "info", 3, PAD + 30, y, W - 2 * PAD - 60, 34, 20, 0xF0E6C8, false);
    if (t.kind == "time") info.text = "In another time: " + t.era + ".";
    else info.text = (t.time != "" ? "The journey takes " + t.time + "." : "") + "   Cost: " + t.cost + " gold (you have " + t.gold + ").";
    y += 50;
    if (t.kind != "time") {
        var opts = [
            [t.cautious ? "Travel cautiously" : "Travel recklessly", "cautious", true],
            [t.inn ? "Stay at inns" : "Camp out", "inn", true],
            [t.ship ? "By ship when it helps" : "No ships", "ship", true],
            [t.horse && t.horseHere ? "On horseback" : (t.horseHere ? "On foot (horse left)" : "On foot"), "horse", t.horseHere]
        ];
        for (var i = 0; i < opts.length; i++) {
            var ob = button(body, "opt" + i, 10 + i, PAD + 30 + i * 272, y, 260, 34, opts[i][0], null);
            ob.opt = opts[i][1];
            if (opts[i][2]) ob.onRelease = function() { askTravel("opt:" + this.opt, 0); };
            else ob._alpha = 45;
        }
        y += 56;
        // The pace: 1 the natural pace, 10 the fastest the land can be loaded at.
        var sp = text(body, "speed", 20, PAD + 30, y + 4, 520, 30, 18, 0xF0E6C8, false);
        sp.text = "Pace: " + t.speed + " of 10  (" + (t.speed <= 1 ? "walking pace" : t.speed >= 10 ? "as fast as the land loads" : "faster") + ")";
        var minus = button(body, "slower", 21, PAD + 560, y, 60, 32, "-", null);
        minus.onRelease = function() { askTravel("speed", Math.max(1, trip.speed - 1)); };
        var plus = button(body, "faster", 22, PAD + 630, y, 60, 32, "+", null);
        plus.onRelease = function() { askTravel("speed", Math.min(10, trip.speed + 1)); };
        for (var k = 1; k <= 10; k++) box(body, PAD + 710 + k * 22, y + 10, 18, 14, k <= t.speed ? 0xD9B76A : 0x3A3A40, 100);
        y += 56;
    }
    if (t.note != "") {
        var note = text(body, "note", 30, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xA8A090, false);
        note.text = t.note;
        y += 44;
    }
    if (t.kind == "road" || t.kind == "time") {
        var go = button(body, "begin", 40, PAD + 30, y, 240, 38, t.kind == "time" ? "Travel through time" : "Begin the journey", null);
        go.onRelease = function() { skse.SendModEvent("TM_Travel", "go", 0); close(); };
        if (t.kind == "road") {
            var hint = text(body, "hint", 41, PAD + 290, y + 8, W - 2 * PAD - 320, 30, 15, 0x8C877C, false);
            hint.text = "You travel on your own, the days passing; a movement key, or a fight, stops the journey.";
        }
    }
}

// Factions game by game: name, then rank and title, or "not a member".
function drawFactions() {
    var lines = [];
    var game = "";
    for (var i = 0; i < factions.length; i++) {
        var f = factions[i];
        if (f[0] != game) { game = f[0]; lines.push({ header: game }); }
        lines.push({ name: f[1], rank: Number(f[2]), ranks: Number(f[3]), title: f[4] });
    }
    if (!factionsReady) { drawNote("Reading the factions..."); return; }
    if (lines.length == 0) {
        // The build found factions in the converted games, but none of their plugins is in the load order; or it found none.
        drawNote(built != null && built.factions > 0 ? "None of the converted games' plugins with factions to join is loaded."
            : "None of the converted games has factions to join.");
        return;
    }
    var pages = Math.ceil(lines.length / (PER_PAGE * 2));
    if (page >= pages) page = pages - 1;
    var start = page * PER_PAGE * 2;
    for (var n = 0; n < PER_PAGE * 2 && start + n < lines.length; n++) {
        var l = lines[start + n];
        var column = Math.floor(n / PER_PAGE);
        var x = PAD + 20 + column * ((W - 2 * PAD) / 2);
        var y = PAD + 60 + (n % PER_PAGE) * 30;
        if (l.header != undefined) {
            var h = text(body, "h" + n, 100 + n, x, y + 4, 520, 30, 20, 0xD9B76A, true);
            h.text = l.header;
        } else {
            var t = text(body, "f" + n, 100 + n, x + 16, y + 4, 520, 30, 17, l.rank > 0 ? 0xF0E6C8 : 0x8C877C, false);
            t.text = l.name + "  -  " + (l.rank > 0 ? "rank " + l.rank + " of " + l.ranks + (l.title != "" ? ": " + l.title : "") : "not a member");
        }
    }
    var p = text(body, "pageNo", 400, W / 2 - 100, H - PAD - 30, 200, 30, 16, 0x8C877C, false, "center");
    p.text = "page " + (page + 1) + " of " + pages;
}

function wrapped(parent, name, depth, x, y, w, h, size, colour) {
    var t = text(parent, name, depth, x, y, w, h, size, colour, false);
    t.wordWrap = true;
    t.multiline = true;
    return t;
}

// A circle centred on (x, y): eight quarter-octant curves.
function circle(mc, x, y, r) {
    var k = Math.tan(Math.PI / 8) * r;
    var s = Math.sin(Math.PI / 4) * r;
    mc.moveTo(x + r, y);
    mc.curveTo(x + r, y + k, x + s, y + s);
    mc.curveTo(x + k, y + r, x, y + r);
    mc.curveTo(x - k, y + r, x - s, y + s);
    mc.curveTo(x - r, y + k, x - r, y);
    mc.curveTo(x - r, y - k, x - s, y - s);
    mc.curveTo(x - k, y - r, x, y - r);
    mc.curveTo(x + k, y - r, x + s, y - s);
    mc.curveTo(x + r, y - k, x + r, y);
}

// ---- skills: the converted games' skills Skyrim lacks (TMSkills) ----
var skillState = null;
var dfState = [];
var ownedPerks = "";
var perkPoints = 0;
var skillMessage = "";
var skillPick = 0;

function SkillState(line) {
    var p = line.split("#");
    skillState = [];
    if (p[0] != "") {
        var rows = p[0].split(";");
        for (var i = 0; i < rows.length; i++) {
            var v = rows[i].split(",");
            skillState.push([Number(v[0]), Number(v[1]), Number(v[2])]);
        }
    }
    dfState = [];
    if (p[1] != "") {
        var d = p[1].split(";");
        for (var j = 0; j < d.length; j++) {
            var w = d[j].split(",");
            dfState.push([Number(w[0]), Number(w[1])]);
        }
    }
    ownedPerks = p[2];
    perkPoints = Number(p[3]);
    skillMessage = p[4];
    if (tab == 3) draw();
}

function bar(mc, x, y, w, frac) {
    box(mc, x, y, w, 8, 0x3A3A40, 100);
    if (frac > 0) box(mc, x, y, Math.max(2, w * Math.min(1, frac)), 8, 0xD9B76A, 100);
}

// The skills on the left with level and progress; the chosen one on the right with what it does and its perk tree.
function drawSkills() {
    if (built == null || (built.skills.length == 0 && built.df.length == 0)) {
        drawNote("None of the converted games has skills Skyrim lacks (Redguard's are Skyrim's own), so there are no new skills.");
        return;
    }
    if (skillState == null) { drawNote("Reading your skills..."); return; }
    var count = built.skills.length + (built.df.length > 0 ? 1 : 0);
    if (skillPick >= count) skillPick = count - 1;
    var points = text(body, "points", 2, W - PAD - 340, PAD + 66, 320, 28, 18, 0xD9B76A, true, "right");
    points.text = "Perk points: " + perkPoints;
    for (var i = 0; i < count; i++) {
        var row = body.createEmptyMovieClip("row" + i, 10 + i);
        box(row, 0, 0, 470, 30, i == skillPick ? 0x3A3426 : 0x1E1E24, 90);
        row._x = PAD + 20;
        row._y = PAD + 100 + i * 33;
        var n = text(row, "n", 1, 10, 4, 230, 26, 17, i == skillPick ? 0xF0E6C8 : 0xC8C0B0, i == skillPick);
        if (i < built.skills.length) {
            var st = skillState[i];
            n.text = built.skills[i].n;
            var lv = text(row, "l", 2, 236, 4, 50, 26, 17, 0xD9B76A, true, "right");
            lv.text = st != undefined ? String(st[0]) : "";
            if (st != undefined) bar(row, 300, 11, 155, st[0] >= 100 ? 1 : (st[2] > 0 ? st[1] / st[2] : 0));
        } else {
            n.text = "Daggerfall's own skills";
        }
        row.index = i;
        row.onRelease = function() { skillPick = this.index; draw(); };
        row.onRollOver = function() { this._alpha = 80; };
        row.onRollOut = function() { this._alpha = 100; };
    }
    if (skillPick < built.skills.length) drawSkill(skillPick);
    else drawDfSkills();
    if (skillMessage != "") {
        var m = text(body, "msg", 3, PAD + 520, H - PAD - 64, W - 2 * PAD - 680, 28, 16, 0xE8C878, false);
        m.text = skillMessage;
    }
}

function drawSkill(i) {
    var s = built.skills[i];
    var st = skillState[i];
    var x0 = PAD + 520;
    var w0 = W - PAD - 20 - x0;
    var head = text(body, "sName", 100, x0, PAD + 96, w0, 34, 24, 0xD9B76A, true);
    head.text = s.n + (st != undefined ? "  " + st[0] : "");
    var from = wrapped(body, "sFrom", 101, x0, PAD + 130, w0, 44, 15, 0x8C877C);
    from.text = "From " + s.f + ". Rises with " + s.r + ".";
    var does = wrapped(body, "sDoes", 102, x0, PAD + 172, w0, 44, 15, 0xC8C0B0);
    does.text = "Its level: " + s.e + ", growing with each level.";
    var xp = text(body, "sXp", 103, x0, PAD + 214, w0, 24, 15, 0xA8A090, false);
    if (st == undefined) xp.text = "";
    else if (st[0] >= 100) xp.text = "Mastered.";
    else xp.text = "Experience " + st[1] + " of " + st[2] + " toward " + (st[0] + 1) + ".";
    // The perk tree: each perk a column, its ranks one below the other.
    var chains = [];
    for (var k = 0; k < built.perks.length; k++) {
        var p = built.perks[k];
        if (p[0] != i) continue;
        if (p[2] < 0) chains.push([k]);
        else for (var c = 0; c < chains.length; c++) if (chains[c][chains[c].length - 1] == p[2]) chains[c].push(k);
    }
    var links = body.createEmptyMovieClip("perkLinks", 110);
    var colW = Math.min(196, Math.floor((w0 - (chains.length - 1) * 10) / Math.max(1, chains.length)));
    var top = PAD + 248;
    var depth = 120;
    for (var c2 = 0; c2 < chains.length; c2++) {
        for (var r = 0; r < chains[c2].length; r++) {
            var n = chains[c2][r];
            var q = built.perks[n];
            var x = x0 + c2 * (colW + 10);
            var y = top + r * 116;
            var owned = ownedPerks.charAt(n) == "1";
            var reach = st != undefined && st[0] >= q[1];
            var before = q[2] < 0 || ownedPerks.charAt(q[2]) == "1";
            var card = body.createEmptyMovieClip("perk" + n, depth++);
            box(card, 0, 0, colW, 104, owned ? 0x4A4026 : (reach && before ? 0x2A2A30 : 0x18181C), 95);
            card._x = x;
            card._y = y;
            var name = text(card, "n", 1, 8, 4, colW - 16, 24, 16, owned ? 0xF0E6C8 : (reach ? 0xD9B76A : 0x8C877C), true);
            name.text = q[3] + (q[5] > 1 ? " " + q[4] : "");
            var need = text(card, "r", 2, 8, 24, colW - 16, 20, 13, 0x8C877C, false);
            need.text = s.n + " " + q[1] + (owned ? "  -  taken" : "");
            var desc = wrapped(card, "d", 3, 8, 42, colW - 16, 40, 13, 0xC8C0B0);
            desc.text = q[6];
            if (!owned && reach && before) {
                var take = button(card, "take", 4, colW - 78, 76, 70, 24, "Take", null);
                take.perk = n;
                take.onRelease = function() { skse.SendModEvent("TM_Skills", "take:" + this.perk, 0); };
                take.lineStyle(1, 0xD9B76A, 100);
                take.moveTo(0, 0); take.lineTo(70, 0); take.lineTo(70, 24); take.lineTo(0, 24); take.lineTo(0, 0);
            }
            if (r > 0) {
                links.lineStyle(2, owned ? 0xD9B76A : 0x4A4A52, 100);
                links.moveTo(x + colW / 2, y - 12);
                links.lineTo(x + colW / 2, y);
            }
        }
    }
}

// Daggerfall's own skills (DFSkill<name>): levels and progress, raised as Daggerfall raises them.
function drawDfSkills() {
    var x0 = PAD + 520;
    var w0 = W - PAD - 20 - x0;
    var head = text(body, "sName", 100, x0, PAD + 96, w0, 34, 24, 0xD9B76A, true);
    head.text = "Daggerfall's own skills";
    var about = wrapped(body, "sAbout", 101, x0, PAD + 130, w0, 64, 15, 0x8C877C);
    about.text = "Raised as Daggerfall raises them: a language by calming the creatures that speak it, Etiquette and Streetwise by talking down nobles and the underworld.";
    for (var i = 0; i < built.df.length; i++) {
        var y = PAD + 200 + i * 30;
        var d = dfState[i];
        var n = text(body, "df" + i, 120 + i * 2, x0, y, 240, 26, 17, 0xF0E6C8, false);
        n.text = built.df[i];
        var lv = text(body, "dl" + i, 121 + i * 2, x0 + 240, y, 120, 26, 17, 0xD9B76A, true, "right");
        if (d == undefined || d[0] < 0) lv.text = "not loaded";
        else {
            lv.text = String(d[0]);
            bar(body, x0 + 380, y + 8, 180, d[0] >= 100 ? 1 : d[1] / 100);
        }
    }
}

// ---- passives: the web (TMPassives) ----
var webTaken = null;
var webFree = 0;
var webStart = -1;
var webSign = -1;
var webGates = "";
var webMessage = "";
var webLevel = 1;
var webHover = -1;
var webInfo = null;
var NODE_SIZE = [5, 9, 12, 10];
var NODE_KIND = ["", "Notable", "Keystone", "Birthsign"];

function WebState(line) {
    var p = line.split("#");
    webTaken = p[0];
    webFree = Number(p[1]);
    webStart = Number(p[2]);
    webSign = Number(p[3]);
    webGates = p[4];
    webMessage = p[5];
    webLevel = Number(p[6]);
    if (tab == 4) draw();
}

function isTaken(n) { return webTaken.charAt(n) == "1"; }

// What the player could take now: the start (theirs, or any sign without one), or a node touching one taken.
function reachable() {
    var reach = [];
    var L = built.links;
    for (var i = 0; i < L.length; i += 2) {
        if (isTaken(L[i])) reach[L[i + 1]] = true;
        if (isTaken(L[i + 1])) reach[L[i]] = true;
    }
    return reach;
}

function canTake(n, reach) {
    if (isTaken(n)) return false;
    var node = built.nodes[n];
    if (webStart < 0) return node[2] == 3 && (webSign < 0 || node[6] == webSign);
    return reach[n] == true && webFree > 0 && webGates.charAt(n) == "1";
}

// The web: links, then nodes (taken gold, takeable bright-edged, closed by a faction rank red), the signs named round
// the edge; hovering a node shows it on the right, a click takes it.
function drawWeb() {
    if (built == null || built.nodes.length == 0) { drawNote("This menu was built without the passive web."); return; }
    if (webTaken == null) { drawNote("Reading your passive web..."); return; }
    var reach = reachable();
    var lines = body.createEmptyMovieClip("lines", 1);
    var L = built.links;
    for (var i = 0; i < L.length; i += 2) {
        var a = built.nodes[L[i]];
        var b = built.nodes[L[i + 1]];
        var both = isTaken(L[i]) && isTaken(L[i + 1]);
        var one = isTaken(L[i]) || isTaken(L[i + 1]);
        lines.lineStyle(both ? 3 : 1, both ? 0xD9B76A : (one ? 0x8C7A4A : 0x44444C), 100);
        lines.moveTo(a[0], a[1]);
        lines.lineTo(b[0], b[1]);
    }
    for (var n = 0; n < built.nodes.length; n++) {
        var node = built.nodes[n];
        var r = NODE_SIZE[node[2]];
        var taken = isTaken(n);
        var open = webGates.charAt(n) == "1";
        var can = canTake(n, reach);
        var c = body.createEmptyMovieClip("node" + n, 10 + n);
        // A wider, invisible hit area for the small nodes.
        c.beginFill(0x000000, 0);
        circle(c, 0, 0, r + 4);
        c.endFill();
        var edge = node[2] == 2 ? 0xE0A040 : (node[2] == 1 ? 0xC8B88A : (node[2] == 3 ? 0x90B0E0 : 0x6A6A72));
        c.lineStyle(can ? 2 : 1, can ? 0xF0E6C8 : edge, 100);
        c.beginFill(taken ? 0xD9B76A : (!open ? 0x5A2A2A : (can ? 0x5A5040 : 0x24242A)), 100);
        circle(c, 0, 0, r);
        c.endFill();
        c._x = node[0];
        c._y = node[1];
        c.index = n;
        c.onRollOver = function() { webHover = this.index; drawWebInfo(); };
        c.onRollOut = function() { if (webHover == this.index) webHover = -1; drawWebInfo(); };
        c.onRelease = function() { skse.SendModEvent("TM_Web", "take:" + this.index, 0); };
        if (node[2] == 3) {
            // The sign's name outside the ring, along its spoke.
            var dx = node[0] - built.cx;
            var dy = node[1] - built.cy;
            var len = Math.sqrt(dx * dx + dy * dy);
            // Beyond the outer notable: to its left or right on the sides, above or below at the top and bottom.
            var cos = dx / len;
            var sin = dy / len;
            var lx = built.cx + cos * 254;
            var ly = built.cy + sin * 254 - 9 + sin * 8;
            var lw = 110;
            var at = cos < -0.3 ? lx - lw : (cos > 0.3 ? lx : lx - lw / 2);
            var label = text(body, "sign" + n, 300 + n, at, ly, lw, 20, 12, node[6] == webSign ? 0xF0E6C8 : 0x90A0B8, node[6] == webSign,
                cos < -0.3 ? "right" : (cos > 0.3 ? "left" : "center"));
            label.text = built.signs[node[6]];
        }
    }
    var reset = button(body, "reset", 600, W - PAD - 270, H - PAD - 34, 130, 34, "Reset", null);
    reset.onRelease = function() { skse.SendModEvent("TM_Web", "reset", 0); };
    drawWebInfo();
}

// The right panel: points and start; the node under the mouse; or, with none, what has been taken in all.
function drawWebInfo() {
    if (webInfo != null) webInfo.removeMovieClip();
    webInfo = body.createEmptyMovieClip("info", 500);
    var x0 = 800;
    var w0 = W - PAD - 10 - x0;
    var pts = text(webInfo, "pts", 1, x0, PAD + 70, w0, 28, 20, 0xD9B76A, true);
    pts.text = webFree + " of " + webLevel + " points free";
    var st = wrapped(webInfo, "start", 2, x0, PAD + 100, w0, 44, 15, 0xA8A090);
    if (webStart >= 0) st.text = "Begun at " + built.nodes[webStart][3] + ". Take nodes next to those you have; each level brings a point.";
    else if (webSign >= 0) st.text = "Born under the " + built.signs[webSign] + ": begin there (it is free).";
    else st.text = "No birthsign is known: begin at whichever sign you choose (it is free).";
    var y = PAD + 156;
    if (webHover >= 0) {
        var node = built.nodes[webHover];
        var name = text(webInfo, "name", 3, x0, y, w0, 28, 19, 0xF0E6C8, true);
        name.text = node[3];
        y += 28;
        if (NODE_KIND[node[2]] != "") {
            var kind = text(webInfo, "kind", 4, x0, y, w0, 22, 14, node[2] == 2 ? 0xE0A040 : 0xC8B88A, false);
            kind.text = NODE_KIND[node[2]];
            y += 22;
        }
        if (node[4] != node[3]) {
            var desc = wrapped(webInfo, "desc", 5, x0, y, w0, 64, 15, 0xC8C0B0);
            desc.text = node[4];
            y += 66;
        }
        if (node[5] != "") {
            var gate = wrapped(webInfo, "gate", 6, x0, y, w0, 64, 14, webGates.charAt(webHover) == "1" ? 0x8C877C : 0xD07060);
            gate.text = node[5];
            y += 66;
        }
        var state = text(webInfo, "state", 7, x0, y, w0, 22, 14, 0xA8A090, false);
        if (isTaken(webHover)) state.text = "Taken.";
        else if (canTake(webHover, reachable())) state.text = "Click to take it.";
        else if (webStart < 0) state.text = "Begin at your birthsign first.";
        else if (webGates.charAt(webHover) != "1") state.text = "Your rank is too low for it.";
        else if (webFree < 1) state.text = "No points free.";
        else state.text = "Take a node next to it first.";
    } else {
        // Totals of what is taken: the small nodes' stats, then the notables and keystones.
        var sums = [];
        var named = [];
        for (var n = 0; n < built.nodes.length; n++) {
            if (!isTaken(n)) continue;
            var nd = built.nodes[n];
            if (nd[7] >= 0) sums[nd[7]] = (sums[nd[7]] == undefined ? 0 : sums[nd[7]]) + nd[8];
            if (nd[2] == 1 || nd[2] == 2) named.push(nd[3]);
        }
        var stats = [];
        for (var s = 0; s < built.stats.length; s++) if (sums[s] != undefined) stats.push("+" + Math.round(sums[s] * 10) / 10 + " " + built.stats[s]);
        var total = wrapped(webInfo, "total", 8, x0, y, w0, 240, 14, 0xC8C0B0);
        total.text = stats.length == 0 && named.length == 0 ? "Nothing taken yet."
            : stats.join(", ") + (named.length > 0 ? "\n\n" + named.join(", ") : "");
    }
    if (webMessage != "") {
        var m = wrapped(webInfo, "msg", 9, x0, H - PAD - 110, w0, 44, 15, 0xE8C878);
        m.text = webMessage;
    }
}

draw();
skse.SendModEvent("TM_MenuReady", "", 0);
