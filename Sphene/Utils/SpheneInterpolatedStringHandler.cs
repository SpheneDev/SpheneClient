using System.Runtime.CompilerServices;
using System.Text;

namespace Sphene.Utils;

[InterpolatedStringHandler]
public readonly ref struct SpheneInterpolatedStringHandler
{
    readonly StringBuilder _logMessageStringbuilder;

    public SpheneInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _logMessageStringbuilder = new StringBuilder(literalLength);
    }

    public void AppendLiteral(string s)
    {
        _logMessageStringbuilder.Append(s);
    }

    public void AppendFormatted<T>(T t)
    {
        _logMessageStringbuilder.Append(t?.ToString());
    }

    public string BuildMessage() => _logMessageStringbuilder.ToString();
}
