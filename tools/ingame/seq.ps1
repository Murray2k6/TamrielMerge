param([string] $Name = 'seq', [int] $Frames = 8, [int] $Ms = 1000, [int] $Cols = 4, [int] $W = 480, [double] $Gamma = 0.45, [string] $Crop = '')
# Captures $Frames screenshots $Ms apart and tiles them (brightened) into <Name>.png. -Crop "x0,y0,x1,y1" of the 1920x1080 screen.
. "$PSScriptRoot\lib.ps1"
Front; Add-Type -AssemblyName System.Drawing
for ($i = 0; $i -lt $Frames; $i++) { $b = New-Object System.Drawing.Bitmap 1920, 1080; $gr = [System.Drawing.Graphics]::FromImage($b); $gr.CopyFromScreen(0, 0, 0, 0, $b.Size); $b.Save("$Shots\_f$i.png"); $gr.Dispose(); $b.Dispose(); Start-Sleep -Milliseconds $Ms }
$py = @"
from PIL import Image
import math
n=$Frames; cols=$Cols; w=$W; crop='$Crop'
ims=[]
for i in range(n):
    im=Image.open(r'$Shots\_f%d.png'%i).convert('RGB')
    if crop: im=im.crop(tuple(int(v) for v in crop.split(',')))
    h=int(im.height*w/im.width)
    ims.append(im.resize((w,h)).point(lambda v: min(255,int((v/255.0)**$Gamma*255))))
h=ims[0].height
o=Image.new('RGB',(cols*w,math.ceil(n/cols)*h))
for i,im in enumerate(ims): o.paste(im,((i%cols)*w,(i//cols)*h))
o.save(r'$Shots\$Name.png')
"@
$py | python -
"saved $Shots\$Name.png"
