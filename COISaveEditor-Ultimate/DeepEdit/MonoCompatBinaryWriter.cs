using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace COISaveEditorUltimate.DeepEdit;

/// <summary>
/// BinaryWriter wrapper that rewrites .NET 8 assembly-qualified type name strings
/// to use Mono/Unity-compatible assembly references.
///
/// Problem: our tool runs on .NET 8 where System.Boolean lives in
/// "System.Private.CoreLib, Version=8.0.0.0, …". The game runs on Unity/Mono
/// where it lives in "mscorlib, Version=4.0.0.0, …". When BlobWriter writes
/// a generic type like Event&lt;Fix32, bool&gt;, the AssemblyQualifiedName embeds
/// "System.Private.CoreLib" into the type string, which the game can't resolve.
///
/// Fix: override Write(string) and replace the assembly reference in any string
/// that contains "System.Private.CoreLib".
/// </summary>
internal sealed class MonoCompatBinaryWriter : BinaryWriter
{
    // .NET 8 BCL assembly reference (inside AQN strings)
    private const string Net8CoreLib =
        "System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";

    // Mono/Unity BCL assembly reference
    private const string MonoCoreLib =
        "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    public MonoCompatBinaryWriter(Stream output)
        : base(output, Encoding.UTF8, leaveOpen: true)
    {
    }

    public override void Write(string value)
    {
        if (value.Contains("System.Private.CoreLib", StringComparison.Ordinal))
        {
            value = value.Replace(Net8CoreLib, MonoCoreLib, StringComparison.Ordinal);
        }
        base.Write(value);
    }
}
