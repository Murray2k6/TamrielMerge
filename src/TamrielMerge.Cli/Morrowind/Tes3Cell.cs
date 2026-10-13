using System.Numerics;

namespace TamrielMerge.Morrowind;

/// <summary>A reference placed in a Morrowind cell (one FRMR run of a CELL record).</summary>
public sealed class Tes3Ref
{
    public uint RefNum;
    public string BaseId = "";
    public float Scale = 1;
    public Vector3 Position, Rotation;
    /// <summary>Door destination (DODT position/rotation; DNAM interior cell name, absent for exterior).</summary>
    public (Vector3 Position, Vector3 Rotation)? DoorDestination;
    public string? DoorDestinationCell;
    public int LockLevel;
    public string? Key, Trap, Owner, OwnerFaction, Global, Soul;
    public int Count = 1;
    public bool Deleted;
    /// <summary>MVRF: a reference moved here from another cell.</summary>
    public bool Moved;
}

/// <summary>A Morrowind cell: interior (named) or exterior (grid of 8192-unit cells), its ambient light and references.</summary>
public sealed class Tes3Cell
{
    public string Name = "";
    public uint Flags;
    public int GridX, GridY;
    public string? Region;
    public Vector4? Ambient, Sunlight, Fog;
    public float FogDensity;
    public float? WaterHeight;
    public List<Tes3Ref> Refs = [];

    public bool IsInterior => (Flags & 1) != 0;
    public bool HasWater => (Flags & 2) != 0;
    /// <summary>"Behave like exterior": an interior with sky and weather (e.g. Mournhold's districts).</summary>
    public bool QuasiExterior => (Flags & 0x80) != 0;

    public override string ToString() => IsInterior ? Name : $"{(Name.Length > 0 ? Name + " " : "")}({GridX}, {GridY})";

    public static Tes3Cell Parse(Tes3Record rec)
    {
        var cell = new Tes3Cell();
        Tes3Ref? r = null;
        bool moved = false;
        foreach (var f in rec.Fields)
        {
            if (f.Type == "MVRF") { moved = true; continue; }
            if (f.Type == "FRMR")
            {
                r = new Tes3Ref { RefNum = BitConverter.ToUInt32(f.Data, 0), Moved = moved };
                moved = false;
                cell.Refs.Add(r);
                continue;
            }
            if (r is null)
            {
                switch (f.Type)
                {
                    case "NAME": cell.Name = f.Text; break;
                    case "DATA": cell.Flags = BitConverter.ToUInt32(f.Data, 0); cell.GridX = f.Int(4); cell.GridY = f.Int(8); break;
                    case "RGNN": cell.Region = f.Text; break;
                    case "WHGT": cell.WaterHeight = f.Float(); break;
                    case "INTV": cell.WaterHeight = f.Int(); break;
                    case "AMBI":
                        cell.Ambient = Rgba(f.Data, 0); cell.Sunlight = Rgba(f.Data, 4); cell.Fog = Rgba(f.Data, 8);
                        cell.FogDensity = f.Float(12);
                        break;
                }
                continue;
            }
            switch (f.Type)
            {
                case "NAME": r.BaseId = f.Text; break;
                case "XSCL": r.Scale = f.Float(); break;
                case "DATA":
                    r.Position = new Vector3(f.Float(0), f.Float(4), f.Float(8));
                    r.Rotation = new Vector3(f.Float(12), f.Float(16), f.Float(20));
                    break;
                case "DODT":
                    r.DoorDestination = (new Vector3(f.Float(0), f.Float(4), f.Float(8)), new Vector3(f.Float(12), f.Float(16), f.Float(20)));
                    break;
                case "DNAM": r.DoorDestinationCell = f.Text; break;
                case "FLTV": r.LockLevel = f.Int(); break;
                case "KNAM": r.Key = f.Text; break;
                case "TNAM": r.Trap = f.Text; break;
                case "ANAM": r.Owner = f.Text; break;
                case "CNAM": r.OwnerFaction = f.Text; break;
                case "BNAM": r.Global = f.Text; break;
                case "XSOL": r.Soul = f.Text; break;
                case "NAM9": r.Count = f.Int(); break;
                case "DELE": r.Deleted = true; break;
            }
        }
        return cell;
    }

    static Vector4 Rgba(byte[] d, int o) => new(d[o] / 255f, d[o + 1] / 255f, d[o + 2] / 255f, d[o + 3] / 255f);
}
