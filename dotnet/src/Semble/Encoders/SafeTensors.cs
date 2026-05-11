using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Semble.Encoders;

/// <summary>
/// Minimal reader for the safetensors file format. Just enough surface for
/// loading model2vec / potion static-embedding models, which only need to
/// read named float-tensor entries.
/// </summary>
/// <remarks>
/// File layout (little-endian throughout):
///   - 8 bytes: header_length (u64)
///   - header_length bytes: UTF-8 JSON header { name -> { dtype, shape, data_offsets[2] } }
///   - rest: raw tensor data, each tensor at its declared offset
/// See https://github.com/huggingface/safetensors for the spec.
/// </remarks>
internal static class SafeTensors
{
    public sealed record TensorInfo(string Name, string DType, int[] Shape, long DataStart, long DataEnd);

    public sealed class File
    {
        public IReadOnlyDictionary<string, TensorInfo> Tensors { get; }
        public string Path { get; }

        internal File(string path, IReadOnlyDictionary<string, TensorInfo> tensors)
        {
            Path = path;
            Tensors = tensors;
        }

        public static File Open(string path)
        {
            using var stream = System.IO.File.OpenRead(path);
            Span<byte> headerLenBuf = stackalloc byte[8];
            if (stream.Read(headerLenBuf) != 8)
                throw new InvalidDataException($"safetensors: file too short to contain a header length: {path}");
            ulong headerLen = BinaryPrimitives.ReadUInt64LittleEndian(headerLenBuf);
            if (headerLen == 0 || headerLen > int.MaxValue)
                throw new InvalidDataException($"safetensors: implausible header length {headerLen} in {path}");

            var headerBytes = new byte[(int)headerLen];
            int total = 0;
            while (total < headerBytes.Length)
            {
                int read = stream.Read(headerBytes, total, headerBytes.Length - total);
                if (read <= 0)
                    throw new InvalidDataException($"safetensors: short read in header of {path}");
                total += read;
            }

            var dataBaseOffset = 8L + (long)headerLen;
            var tensors = new Dictionary<string, TensorInfo>(StringComparer.Ordinal);

            using var doc = JsonDocument.Parse(headerBytes);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Name == "__metadata__")
                    continue;

                var dtype = entry.Value.GetProperty("dtype").GetString()
                    ?? throw new InvalidDataException($"safetensors: missing dtype for tensor '{entry.Name}'");
                var shape = entry.Value.GetProperty("shape").EnumerateArray()
                    .Select(e => e.GetInt32())
                    .ToArray();
                var offsets = entry.Value.GetProperty("data_offsets");
                long start = offsets[0].GetInt64();
                long end = offsets[1].GetInt64();
                tensors[entry.Name] = new TensorInfo(
                    entry.Name, dtype, shape,
                    DataStart: dataBaseOffset + start,
                    DataEnd: dataBaseOffset + end);
            }

            return new File(path, tensors);
        }

        /// <summary>Read a 2-D float32 tensor by name. Throws on missing or wrong dtype/rank.</summary>
        public float[,] ReadFloat32Matrix(string name)
        {
            if (!Tensors.TryGetValue(name, out var info))
                throw new KeyNotFoundException($"safetensors: tensor '{name}' not found in {Path}");
            if (!string.Equals(info.DType, "F32", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has dtype {info.DType}; only F32 matrices are supported");
            if (info.Shape.Length != 2)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has rank {info.Shape.Length}; expected rank 2");

            int rows = info.Shape[0];
            int cols = info.Shape[1];
            long expectedBytes = (long)rows * cols * sizeof(float);
            long actualBytes = info.DataEnd - info.DataStart;
            if (expectedBytes != actualBytes)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' declares shape {rows}x{cols} ({expectedBytes} B) " +
                    $"but data span is {actualBytes} B");

            var matrix = new float[rows, cols];
            using var stream = System.IO.File.OpenRead(Path);
            stream.Seek(info.DataStart, SeekOrigin.Begin);
            var rowBuf = new byte[cols * sizeof(float)];
            for (int r = 0; r < rows; r++)
            {
                int total = 0;
                while (total < rowBuf.Length)
                {
                    int read = stream.Read(rowBuf, total, rowBuf.Length - total);
                    if (read <= 0)
                        throw new InvalidDataException(
                            $"safetensors: short read in tensor '{name}' at row {r}");
                    total += read;
                }
                for (int c = 0; c < cols; c++)
                {
                    matrix[r, c] = BinaryPrimitives.ReadSingleLittleEndian(
                        rowBuf.AsSpan(c * sizeof(float), sizeof(float)));
                }
            }
            return matrix;
        }

        /// <summary>
        /// Read a 1-D float tensor by name; returns null if absent.
        /// Accepts F32 or F64 -- F64 values are narrowed to F32 on read.
        /// </summary>
        public float[]? ReadFloat32VectorOrNull(string name)
        {
            if (!Tensors.TryGetValue(name, out var info))
                return null;
            bool isF32 = string.Equals(info.DType, "F32", StringComparison.Ordinal);
            bool isF64 = string.Equals(info.DType, "F64", StringComparison.Ordinal);
            if (!isF32 && !isF64)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has dtype {info.DType}; expected F32 or F64");
            if (info.Shape.Length != 1)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has rank {info.Shape.Length}; expected rank 1");
            int n = info.Shape[0];
            int elementBytes = isF64 ? sizeof(double) : sizeof(float);
            var bytes = new byte[n * elementBytes];
            using var stream = System.IO.File.OpenRead(Path);
            stream.Seek(info.DataStart, SeekOrigin.Begin);
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read <= 0)
                    throw new InvalidDataException($"safetensors: short read in vector '{name}'");
                total += read;
            }
            var vec = new float[n];
            if (isF64)
            {
                for (int i = 0; i < n; i++)
                    vec[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(
                        bytes.AsSpan(i * sizeof(double), sizeof(double)));
            }
            else
            {
                for (int i = 0; i < n; i++)
                    vec[i] = BinaryPrimitives.ReadSingleLittleEndian(
                        bytes.AsSpan(i * sizeof(float), sizeof(float)));
            }
            return vec;
        }

        /// <summary>Read a 1-D int64 tensor by name; returns null if absent.</summary>
        public long[]? ReadInt64VectorOrNull(string name)
        {
            if (!Tensors.TryGetValue(name, out var info))
                return null;
            if (!string.Equals(info.DType, "I64", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has dtype {info.DType}; expected I64");
            if (info.Shape.Length != 1)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' has rank {info.Shape.Length}; expected rank 1");
            int n = info.Shape[0];
            var bytes = new byte[n * sizeof(long)];
            using var stream = System.IO.File.OpenRead(Path);
            stream.Seek(info.DataStart, SeekOrigin.Begin);
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read <= 0)
                    throw new InvalidDataException($"safetensors: short read in vector '{name}'");
                total += read;
            }
            var vec = new long[n];
            for (int i = 0; i < n; i++)
                vec[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(i * sizeof(long), sizeof(long)));
            return vec;
        }
    }
}
