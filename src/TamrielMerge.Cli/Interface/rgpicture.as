// Redguard's ViewGXA (TamrielMerge; Redguard Preservation engine/SOUP.html: one GXA image drawn over the world frame
// while the task lasts): the picture over the game, as large as the screen allows at its own shape. ActionScript 2
// for Skyrim's Scaleform, compiled with FFDec (Skyrim.Ui.FlashMenus) and opened by TMRgState through SKSE's
// UI.OpenCustomMenu. It asks for the picture when loaded (mod event TM_PictureReady) and gets Show(path, w, h): the
// image's texture as Skyrim's book pages name it (img://Textures/...png for the .dds) and its size. TMRgState closes it
// when the task's time is up.

var root = this;
var W = 1280;
var H = 720;

function Show(path, w, h) {
    var s = Math.min(W / w, H / h);
    var pw = Math.floor(w * s);
    var ph = Math.floor(h * s);
    root.createTextField("pic", 2, Math.floor((W - pw) / 2), Math.floor((H - ph) / 2), pw + 4, ph + 4);
    var t = root.pic;
    t.html = true;
    t.selectable = false;
    t.multiline = false;
    t.htmlText = "<img src='" + path + "' width='" + pw + "' height='" + ph + "'>";
}

skse.SendModEvent("TM_PictureReady", "", 0);
