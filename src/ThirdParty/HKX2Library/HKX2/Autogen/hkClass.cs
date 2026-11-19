using System;
using System.Xml.Linq;

namespace HKX2
{
    // hkClass Signatire: 0x75585ef6 size: 80 flags: FLAGS_NONE

    // m_name m_class:  Type.TYPE_CSTRING Type.TYPE_VOID arrSize: 0 offset: 0 flags: FLAGS_NONE enum: 
    // m_parent m_class: hkClass Type.TYPE_POINTER Type.TYPE_STRUCT arrSize: 0 offset: 8 flags: FLAGS_NONE enum: 
    // m_objectSize m_class:  Type.TYPE_INT32 Type.TYPE_VOID arrSize: 0 offset: 16 flags: FLAGS_NONE enum: 
    // m_numImplementedInterfaces m_class:  Type.TYPE_INT32 Type.TYPE_VOID arrSize: 0 offset: 20 flags: FLAGS_NONE enum: 
    // m_declaredEnums m_class: hkClassEnum Type.TYPE_SIMPLEARRAY Type.TYPE_STRUCT arrSize: 0 offset: 24 flags: FLAGS_NONE enum: 
    // m_declaredMembers m_class: hkClassMember Type.TYPE_SIMPLEARRAY Type.TYPE_STRUCT arrSize: 0 offset: 40 flags: FLAGS_NONE enum: 
    // m_defaults m_class:  Type.TYPE_POINTER Type.TYPE_VOID arrSize: 0 offset: 56 flags: SERIALIZE_IGNORED|FLAGS_NONE enum: 
    // m_attributes m_class: hkCustomAttributes Type.TYPE_POINTER Type.TYPE_STRUCT arrSize: 0 offset: 64 flags: SERIALIZE_IGNORED|FLAGS_NONE enum: 
    // m_flags m_class:  Type.TYPE_FLAGS Type.TYPE_UINT32 arrSize: 0 offset: 72 flags: FLAGS_NONE enum: FlagValues
    // m_describedVersion m_class:  Type.TYPE_INT32 Type.TYPE_VOID arrSize: 0 offset: 76 flags: FLAGS_NONE enum: 
    public partial class hkClass : IHavokObject, IEquatable<hkClass?>
    {
        public string m_name { set; get; } = "";
        public hkClass? m_parent { set; get; }
        public int m_objectSize { set; get; }
        public int m_numImplementedInterfaces { set; get; }
        public object? m_declaredEnums { set; get; }
        public object? m_declaredMembers { set; get; }
        private object? m_defaults { set; get; }
        private hkCustomAttributes? m_attributes { set; get; }
        public uint m_flags { set; get; }
        public int m_describedVersion { set; get; }

        public virtual uint Signature { set; get; } = 0x75585ef6;

        public virtual void Read(PackFileDeserializer des, BinaryReaderEx br)
        {
            m_name = des.ReadCString(br);
            m_parent = des.ReadClassPointer<hkClass>(br);
            m_objectSize = br.ReadInt32();
            m_numImplementedInterfaces = br.ReadInt32();
            // m_declaredEnums is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void Write(PackFileSerializer s, BinaryWriterEx bw)
        {
            s.WriteCString(bw, m_name);
            s.WriteClassPointer(bw, m_parent);
            bw.WriteInt32(m_objectSize);
            bw.WriteInt32(m_numImplementedInterfaces);
            // m_declaredEnums is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void ReadXml(XmlDeserializer xd, XElement xe)
        {
            m_name = xd.ReadString(xe, nameof(m_name));
            m_parent = xd.ReadClassPointer<hkClass>(xe, nameof(m_parent));
            m_objectSize = xd.ReadInt32(xe, nameof(m_objectSize));
            m_numImplementedInterfaces = xd.ReadInt32(xe, nameof(m_numImplementedInterfaces));
            // m_declaredEnums is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public virtual void WriteXml(XmlSerializer xs, XElement xe)
        {
            xs.WriteString(xe, nameof(m_name), m_name);
            xs.WriteClassPointer(xe, nameof(m_parent), m_parent);
            xs.WriteNumber(xe, nameof(m_objectSize), m_objectSize);
            xs.WriteNumber(xe, nameof(m_numImplementedInterfaces), m_numImplementedInterfaces);
            // m_declaredEnums is a TYPE_SIMPLEARRAY, which isn't supported; nothing after it is ever reached
            throw new NotImplementedException("TPYE_SIMPLEARRAY");
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as hkClass);
        }

        public bool Equals(hkClass? other)
        {
            return other is not null &&
                   (m_name is null && other.m_name is null || m_name == other.m_name || m_name is null && other.m_name == "" || m_name == "" && other.m_name is null) &&
                   ((m_parent is null && other.m_parent is null) || (m_parent is not null && other.m_parent is not null && m_parent.Equals((IHavokObject)other.m_parent))) &&
                   m_objectSize.Equals(other.m_objectSize) &&
                   m_numImplementedInterfaces.Equals(other.m_numImplementedInterfaces) &&
                   // not filled by the readers (TYPE_SIMPLEARRAY); a null one threw NullReferenceException here before, and still does
                   (m_declaredEnums ?? throw new NullReferenceException("hkClass.m_declaredEnums is null")).Equals(other.m_declaredEnums) &&
                   (m_declaredMembers ?? throw new NullReferenceException("hkClass.m_declaredMembers is null")).Equals(other.m_declaredMembers) &&
                   m_flags.Equals(other.m_flags) &&
                   m_describedVersion.Equals(other.m_describedVersion) &&
                   Signature == other.Signature; ;
        }

        public override int GetHashCode()
        {
            var hashcode = new HashCode();
            hashcode.Add(m_name);
            hashcode.Add(m_parent);
            hashcode.Add(m_objectSize);
            hashcode.Add(m_numImplementedInterfaces);
            hashcode.Add(m_declaredEnums);
            hashcode.Add(m_declaredMembers);
            hashcode.Add(m_flags);
            hashcode.Add(m_describedVersion);
            hashcode.Add(Signature);
            return hashcode.ToHashCode();
        }
    }
}

