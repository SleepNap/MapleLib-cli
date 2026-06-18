namespace MapleLib.XmlImgPatcher.Model
{
    /// <summary>
    /// Logical value type of a node parsed from server XML.
    /// Mirrors the subset of WzPropertyType the diff format actually uses.
    /// </summary>
    public enum ValueType
    {
        Sub,        // <imgdir> container
        String,     // <string ... value="..."/>
        Int,        // <int ... value="..."/>
        Short,      // <short ... value="..."/>
        Long,       // <long ... value="..."/>
        Float,      // <float ... value="..."/>
        Double,     // <double ... value="..."/>
        Vector,     // <vector ... x="..." y="..."/>
        Null,       // <null .../>
    }
}
