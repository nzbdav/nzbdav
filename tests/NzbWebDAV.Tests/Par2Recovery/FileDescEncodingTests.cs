using System.Text;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

public class FileDescEncodingTests
{
    static FileDescEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData("한국어_예능.mkv")]
    [InlineData("日本語テスト.mkv")]
    [InlineData("Show_한국어_mix.mkv")]
    [InlineData("ascii-only.mkv")]
    public void ParseBody_DecodesUtf8Names(string fileName)
    {
        var desc = Parse(BuildBody(fileName, Encoding.UTF8));
        Assert.Equal(fileName, desc.FileName);
    }

    [Fact]
    public void ParseBody_FallsBackToWindows1252ForLegacyNames()
    {
        var fileName = "Café.mkv";
        var encoding1252 = Encoding.GetEncoding(1252);
        var desc = Parse(BuildBody(fileName, encoding1252));
        Assert.Equal(fileName, desc.FileName);
    }

    [Fact]
    public void ParseBody_StripsUtf8Bom()
    {
        var fileName = "bom-test.mkv";
        var nameBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(fileName)).ToArray();
        var body = BuildBodyFromNameBytes(nameBytes);
        var desc = Parse(body);
        Assert.Equal(fileName, desc.FileName);
    }

    private static TestFileDesc Parse(byte[] body)
    {
        var header = new Par2PacketHeader
        {
            Magic = "PAR2\0PKT"u8.ToArray(),
            PacketLength = (ulong)(64 + body.Length),
            PacketHash = new byte[16],
            RecoverySetID = new byte[16],
            PacketType = Encoding.ASCII.GetBytes(FileDesc.PacketType.PadRight(16, '\0')[..16]),
        };
        var desc = new TestFileDesc(header);
        desc.Parse(body);
        return desc;
    }

    private static byte[] BuildBody(string fileName, Encoding encoding) =>
        BuildBodyFromNameBytes(encoding.GetBytes(fileName));

    private static byte[] BuildBodyFromNameBytes(byte[] nameBytes)
    {
        var paddedLen = ((nameBytes.Length + 3) / 4) * 4;
        var body = new byte[56 + paddedLen];
        nameBytes.CopyTo(body, 56);
        return body;
    }

    private sealed class TestFileDesc(Par2PacketHeader header) : FileDesc(header)
    {
        public void Parse(byte[] body) => ParseBody(body);
    }
}
