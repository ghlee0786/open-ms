using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Sim.Core.Airflow;

/// <summary>배열 하나(본문 안, little-endian). Dtype: f4 · i4 · u1.</summary>
public sealed record AirflowArray(string Dtype, int[] Shape, float[]? F4 = null, int[]? I4 = null, byte[]? U1 = null)
{
    public int Count => Shape.Aggregate(1, (a, b) => a * b);
}

/// <summary>메시지 한 개: 머리(JSON) + 이름 → 배열.</summary>
public sealed record AirflowMessage(JsonObject Header, IReadOnlyDictionary<string, AirflowArray> Arrays)
{
    public string Type => Header["type"]?.GetValue<string>() ?? Header["cmd"]?.GetValue<string>() ?? "";
}

/// <summary>
/// GPU 가상 풍동 IPC 형식(schema mns.airflow-ipc/1) — tools/airflow/protocol.py 와 같다.
/// 메시지 = [u32 머리 길이][u32 본문 길이][머리 JSON UTF-8][본문], little-endian. 머리 "arrays" = [{name, dtype, shape, offset, nbytes}].
/// 같은 형식의 파일(.mnsf)을 OpenFOAM 재생 시퀀스 프레임으로도 쓴다(<see cref="FlowSequence"/>).
/// </summary>
public static class AirflowProtocol
{
    public const string Schema = "mns.airflow-ipc/1";
    public const int MaxHeader = 1 << 20;
    public const int MaxPayload = 1 << 30;

    public static byte[] Encode(JsonObject header, IReadOnlyDictionary<string, AirflowArray>? arrays = null)
    {
        var h = (JsonObject)header.DeepClone();
        var meta = new JsonArray();
        using var body = new MemoryStream();
        foreach (var (name, a) in arrays ?? new Dictionary<string, AirflowArray>())
        {
            long off = body.Position;
            switch (a.Dtype)
            {
                case "f4": foreach (var v in a.F4!) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); body.Write(b); } break;
                case "i4": foreach (var v in a.I4!) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); body.Write(b); } break;
                case "u1": body.Write(a.U1!); break;
                default: throw new InvalidDataException($"배열 {name}: dtype {a.Dtype}");
            }
            meta.Add(new JsonObject
            {
                ["name"] = name, ["dtype"] = a.Dtype, ["shape"] = new JsonArray(a.Shape.Select(s => (JsonNode)s).ToArray()),
                ["offset"] = off, ["nbytes"] = body.Position - off,
            });
        }
        h["arrays"] = meta;
        var hb = Encoding.UTF8.GetBytes(h.ToJsonString());
        var res = new byte[8 + hb.Length + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(0), (uint)hb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(4), (uint)body.Length);
        hb.CopyTo(res, 8);
        body.ToArray().CopyTo(res, 8 + hb.Length);
        return res;
    }

    public static AirflowMessage Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < 8) throw new InvalidDataException("메시지가 8 바이트보다 짧습니다");
        uint hl = BinaryPrimitives.ReadUInt32LittleEndian(message), pl = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]);
        if (message.Length != 8L + hl + pl) throw new InvalidDataException($"메시지 길이 불일치: {message.Length} != 8 + {hl} + {pl}");
        return Decode(message.Slice(8, (int)hl), message[(8 + (int)hl)..]);
    }

    public static AirflowMessage Decode(ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> payload)
    {
        var h = JsonNode.Parse(Encoding.UTF8.GetString(headerBytes)) as JsonObject ?? throw new InvalidDataException("머리가 JSON 객체가 아닙니다");
        var arrays = new Dictionary<string, AirflowArray>();
        if (h["arrays"] is JsonArray meta)
            foreach (var mn in meta)
            {
                var m = mn!.AsObject();
                string name = m["name"]!.GetValue<string>(), dt = m["dtype"]!.GetValue<string>();
                var shape = m["shape"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
                long off = m["offset"]!.GetValue<long>(), nb = m["nbytes"]!.GetValue<long>();
                if (off < 0 || nb < 0 || off + nb > payload.Length) throw new InvalidDataException($"배열 {name} 이 본문 밖을 가리킵니다");
                var span = payload.Slice((int)off, (int)nb);
                int count = shape.Aggregate(1, (a, b) => a * b);
                int size = dt switch { "f4" or "i4" => 4, "u1" => 1, _ => throw new InvalidDataException($"배열 {name}: dtype {dt}") };
                if (count * size != nb) throw new InvalidDataException($"배열 {name}: 모양 {string.Join("×", shape)} 과 바이트 {nb} 가 다릅니다");
                arrays[name] = dt switch
                {
                    "f4" => new AirflowArray(dt, shape, F4: ReadF4(span, count)),
                    "i4" => new AirflowArray(dt, shape, I4: ReadI4(span, count)),
                    _ => new AirflowArray(dt, shape, U1: span.ToArray()),
                };
            }
        return new AirflowMessage(h, arrays);
    }

    private static float[] ReadF4(ReadOnlySpan<byte> s, int n)
    {
        if (BitConverter.IsLittleEndian) return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(s).ToArray();
        var r = new float[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(4 * i)..]);
        return r;
    }

    private static int[] ReadI4(ReadOnlySpan<byte> s, int n)
    {
        if (BitConverter.IsLittleEndian) return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(s).ToArray();
        var r = new int[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadInt32LittleEndian(s[(4 * i)..]);
        return r;
    }

    /// <summary>스트림에서 메시지 하나(끝이면 null). 길이 상한을 넘으면 InvalidDataException.</summary>
    public static async Task<AirflowMessage?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[8];
        if (!await ReadExactAsync(s, head, ct)) return null;
        uint hl = BinaryPrimitives.ReadUInt32LittleEndian(head), pl = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (hl > MaxHeader || pl > MaxPayload) throw new InvalidDataException($"메시지가 너무 큽니다(머리 {hl}, 본문 {pl})");
        var hb = new byte[hl];
        var pb = new byte[pl];
        if (!await ReadExactAsync(s, hb, ct) || !await ReadExactAsync(s, pb, ct)) throw new EndOfStreamException("메시지 중간에 연결이 끊겼습니다");
        return Decode(hb, pb);
    }

    public static async Task WriteAsync(Stream s, JsonObject header, IReadOnlyDictionary<string, AirflowArray>? arrays = null, CancellationToken ct = default)
    {
        var b = Encode(header, arrays);
        await s.WriteAsync(b, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException("메시지 중간에 연결이 끊겼습니다");
            }
            got += n;
        }
        return true;
    }
}
