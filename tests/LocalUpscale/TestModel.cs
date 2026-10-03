using System.Text;

// Small deterministic ONNX fixture: nearest 2x Resize, then per-channel Mul.
// Encoding the public protobuf schema here avoids adding an ONNX/Python build dependency.
internal static class TestModel
{
    public static byte[] Create()
    {
        byte[] resize = Join(Text(1, "image"), Text(1, ""), Text(1, "scales"), Text(2, "resized"), Text(4, "Resize"),
            Bytes(5, Attribute("mode", "nearest")), Bytes(5, Attribute("coordinate_transformation_mode", "asymmetric")),
            Bytes(5, Attribute("nearest_mode", "floor")));
        byte[] multiply = Join(Text(1, "resized"), Text(1, "factors"), Text(2, "output"), Text(4, "Mul"));
        byte[] graph = Join(Bytes(1, resize), Bytes(1, multiply), Text(2, "alpha-fixture"),
            Bytes(5, Tensor("scales", [4], [1, 1, 2, 2])), Bytes(5, Tensor("factors", [1, 3, 1, 1], [1, 0.5f, 0.25f])),
            Bytes(11, Value("image")), Bytes(12, Value("output")));
        return Join(Int(1, 8), Text(2, "NAITool-tests"), Bytes(7, graph), Bytes(8, Int(2, 13)));
    }

    private static byte[] Value(string name) => Join(Text(1, name), Bytes(2,
        Bytes(1, Join(Int(1, 1), Bytes(2, Join(Bytes(1, Int(1, 1)), Bytes(1, Int(1, 3)),
            Bytes(1, Text(2, "height")), Bytes(1, Text(2, "width"))))))));
    private static byte[] Attribute(string name, string value) => Join(Text(1, name), Text(4, value), Int(20, 3));
    private static byte[] Tensor(string name, int[] dims, float[] values) => Join(
        Join(dims.Select(d => Int(1, d)).ToArray()), Int(2, 1), Text(8, name), Bytes(9, values.SelectMany(BitConverter.GetBytes).ToArray()));
    private static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    private static byte[] Bytes(int field, byte[] value) => Join(Varint((field << 3) | 2), Varint(value.Length), value);
    private static byte[] Int(int field, int value) => Join(Varint(field << 3), Varint(value));
    private static byte[] Varint(int value)
    {
        var bytes = new List<byte>();
        while (value > 127) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
