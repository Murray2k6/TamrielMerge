using System;
using System.Xml.Linq;

namespace HKX2
{
    // hkClassEnum Signatire: 0x8a3609cf size: 40 flags: FLAGS_NONE

    // m_name m_class:  Type.TYPE_CSTRING Type.TYPE_VOID arrSize: 0 offset: 0 flags: FLAGS_NONE enum: 
    // m_items m_class: hkClassEnumItem Type.TYPE_SIMPLEARRAY Type.TYPE_STRUCT arrSize: 0 offset: 8 flags: FLAGS_NONE enum: 
    // m_attributes m_class: hkCustomAttributes Type.TYPE_POINTER Type.TYPE_STRUCT arrSize: 0 offset: 24 flags: SERIALIZE_IGNORED|FLAGS_NONE enum: 
    // m_flags m_class:  Type.TYPE_FLAGS Type.TYPE_UINT32 arrSize: 0 offset: 32 flags: FLAGS_NONE enum: FlagValues
    public partial class hkClassEnum : IHavokObject, IEquatable<hkClassEnum?>
    {
        public string m_name { set; get; } = "";
        public object? m_items { set; get; }
        private hkCustomAttributes? m_attributes { set; get; }
        public uint m_flags { set; get; }

        public virtual uint Signature { set; get; } = 0x8a3609cf;

        public virtual void Read(PackFileDeserializer des, BinaryReaderEx br)
        {
            m_name = des.ReadCString(br);
            // m_items is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void Write(PackFileSerializer s, BinaryWriterEx bw)
        {
            s.WriteCString(bw, m_name);
            // m_items is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void ReadXml(XmlDeserializer xd, XElement xe)
        {
            m_name = xd.ReadString(xe, nameof(m_name));
            // m_items is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void WriteXml(XmlSerializer xs, XElement xe)
        {
            xs.WriteString(xe, nameof(m_name), m_name);
            // m_items is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as hkClassEnum);
        }

        public bool Equals(hkClassEnum? other)
        {
            return other is not null &&
                   (m_name is null && other.m_name is null || m_name == other.m_name || m_name is null && other.m_name == "" || m_name == "" && other.m_name is null) &&
                   // not filled by the readers (TYPE_SIMPLEARRAY); a null one threw NullReferenceException here before, and still does
                   (m_items ?? throw new NullReferenceException("hkClassEnum.m_items is null")).Equals(other.m_items) &&
                   m_flags.Equals(other.m_flags) &&
                   Signature == other.Signature; ;
        }

        public override int GetHashCode()
        {
            var hashcode = new HashCode();
            hashcode.Add(m_name);
            hashcode.Add(m_items);
            hashcode.Add(m_flags);
            hashcode.Add(Signature);
            return hashcode.ToHashCode();
        }
    }
}

