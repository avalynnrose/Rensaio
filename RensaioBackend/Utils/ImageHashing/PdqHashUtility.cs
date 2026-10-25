using NetVips;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RensaioBackend.Utils.ImageHashing
{
    public static class PdqHashUtility
    {
        private const int DownsampleSize = 64;
        private const int DctSize = 16;
        private const int JaroszWindowDivisor = 128;
        private const int JaroszPasses = 2;
        private const float LumaR = 0.299f;
        private const float LumaG = 0.587f;
        private const float LumaB = 0.114f;
        private static readonly float[] DctMatrix = CreateDctMatrix();

        /// <summary>
        /// Computes PDQ hash + quality from an image file.
        /// </summary>
        public static PdqResult Compute(string fileName)
        {
            using var image = Image.NewFromFile(fileName, access: Enums.Access.Sequential);
            return Compute(image);
        }
        public static PdqResult Compute(Stream stream)
        {
            using var image = Image.NewFromStream(stream, access: Enums.Access.Sequential);
            return Compute(image);
        }
        /// <summary>
        /// Computes PDQ hash + quality from an existing NetVips image.
        /// The supplied image is not disposed.
        /// </summary>
        public static PdqResult Compute(Image image)
        {
            ArgumentNullException.ThrowIfNull(image);

            using var srgb = image.Colourspace(Enums.Interpretation.Srgb);
            using var rgb = srgb.ExtractBand(0,n: 3);
            using var rgb8 = rgb.Cast(Enums.BandFormat.Uchar,shift: true);
            nint memory = rgb8.WriteToMemory(out ulong size);

            try
            {
                if (size > int.MaxValue)
                {
                    throw new InvalidOperationException("Image buffer is too large.");
                }

                var pixels = new byte[(int)size];
                Marshal.Copy(memory, pixels, 0, pixels.Length);
                return ComputeRgb(pixels, rgb8.Width, rgb8.Height);
            }
            finally
            {
                NetVips.NetVips.Free(memory);
            }
        }

        /// <summary>
        /// Fast Hamming-distance comparison.
        /// Returns 0..256.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int Compare(in PdqHash256 left, in PdqHash256 right)
        {
            return PdqHash256.HammingDistance(in left, in right);
        }

        /// <summary>
        /// Convenience comparison for hexadecimal PDQ hashes.
        ///
        /// For hot paths, parse hashes once and use the PdqHash256 overload.
        /// </summary>
        public static int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            return Compare(PdqHash256.Parse(left), PdqHash256.Parse(right));
        }

        private static PdqResult ComputeRgb(ReadOnlySpan<byte> rgb, int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            int pixelCount = checked(width * height);
            int requiredBytes = checked(pixelCount * 3);

            if (rgb.Length < requiredBytes)
            {
                throw new ArgumentException("RGB buffer is smaller than expected.",nameof(rgb));
            }
            float[] buffer1 = ArrayPool<float>.Shared.Rent(pixelCount);
            float[] buffer2 = ArrayPool<float>.Shared.Rent(pixelCount);

            try
            {
                FillLuma(rgb, buffer1, pixelCount);
                int windowX = ComputeJaroszWindow(width);
                int windowY = ComputeJaroszWindow(height);
                JaroszFilter(buffer1, buffer2, height, width, windowX, windowY);
                Span<float> buffer64x64 = stackalloc float[DownsampleSize * DownsampleSize];
                Span<float> buffer16x64 = stackalloc float[DctSize * DownsampleSize];
                Span<float> buffer16x16 = stackalloc float[DctSize * DctSize];

                //
                // Jarosz filtered image -> 64x64.
                //
                Decimate(buffer1, height, width, buffer64x64);
                //
                // Quality is computed from the 64x64
                // image-domain representation.
                //
                int quality = ComputeQuality(buffer64x64);
                //
                // 64x64 -> 16x16 DCT frequencies.
                //
                Dct64To16(buffer64x64, buffer16x64, buffer16x16);
                PdqHash256 hash = Quantize(buffer16x16);
                return new PdqResult(hash, quality);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer1, clearArray: false);
                ArrayPool<float>.Shared.Return(buffer2, clearArray: false);
            }
        }

        private static void FillLuma(ReadOnlySpan<byte> rgb, float[] luma, int pixelCount)
        {
            int source = 0;

            for (int i = 0; i < pixelCount; i++)
            {
                float r = rgb[source];
                float g = rgb[source + 1];
                float b = rgb[source + 2];
                luma[i] = LumaR * r + LumaG * g + LumaB * b;
                source += 3;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeJaroszWindow(int dimension)
        {
            return (dimension + JaroszWindowDivisor - 1) / JaroszWindowDivisor;
        }

        private static void JaroszFilter(float[] buffer1, float[] buffer2, int rows, int columns, int windowX, int windowY)
        {
            //
            // X, Y, X, Y.
            //
            // Two XY box-filter passes produce
            // the tent filter used by PDQ.
            //
            for (int pass = 0; pass < JaroszPasses; pass++)
            {
                BoxRows(buffer1, buffer2, rows, columns, windowX);
                BoxColumns(buffer2, buffer1, rows, columns, windowY);
            }
        }

        private static void BoxRows(float[] input, float[] output, int rows, int columns, int windowSize)
        {
            for (int row = 0; row < rows; row++)
            {
                int offset = row * columns;
                Box1D(input, offset, output, offset, columns, 1, windowSize);
            }
        }

        private static void BoxColumns(float[] input, float[] output, int rows, int columns, int windowSize)
        {
            for (int column = 0; column < columns; column++)
            {
                Box1D(input, column, output, column, rows, columns, windowSize);
            }
        }

        private static void Box1D(float[] input, int inputOffset, float[] output, int outputOffset, int vectorLength, int stride, int fullWindowSize)
        {
            int halfWindowSize = (fullWindowSize + 2) / 2;
            int phase1Count = halfWindowSize - 1;
            int phase2Count = fullWindowSize - halfWindowSize + 1;
            int phase3Count = vectorLength - fullWindowSize;
            int phase4Count = halfWindowSize - 1;

            int left = 0;
            int right = 0;
            int outputIndex = 0;
            float sum = 0.0f;

            int currentWindowSize = 0;

            //
            // Phase 1:
            // accumulate initial partial window.
            //
            for (int i = 0; i < phase1Count; i++)
            {
                sum += input[inputOffset + right];
                currentWindowSize++;
                right += stride;
            }

            //
            // Phase 2:
            // growing window at the left edge.
            //
            for (int i = 0; i < phase2Count; i++)
            {
                sum += input[inputOffset + right];
                currentWindowSize++;
                output[outputOffset + outputIndex] = sum / currentWindowSize;
                right += stride;
                outputIndex += stride;
            }

            //
            // Phase 3:
            // full-size sliding window.
            //
            for (int i = 0;i < phase3Count; i++)
            {
                sum += input[inputOffset + right];
                sum -= input[inputOffset + left];
                output[outputOffset + outputIndex] = sum / currentWindowSize;
                left += stride;
                right += stride;
                outputIndex += stride;
            }

            //
            // Phase 4:
            // shrinking window at right edge.
            //
            for (int i = 0; i < phase4Count; i++)
            {
                sum -= input[inputOffset + left];
                currentWindowSize--;
                output[outputOffset + outputIndex] = sum / currentWindowSize;
                left += stride;
                outputIndex += stride;
            }
        }

        private static void Decimate(float[] input, int rows, int columns, Span<float> output)
        {
            //
            // PDQ samples target pixel centers,
            // rather than corners.
            //
            for (int y = 0; y < DownsampleSize; y++)
            {
                int sourceY = (int)(((y + 0.5) * rows) / DownsampleSize);
                int sourceRow = sourceY * columns;
                int outputRow = y * DownsampleSize;

                for (int x = 0; x < DownsampleSize; x++)
                {
                    int sourceX = (int)( ((x + 0.5) * columns) / DownsampleSize);
                    output[ outputRow + x] = input[sourceRow + sourceX];
                }
            }
        }

        /// <summary>
        /// PDQ image-domain quality metric.
        /// Returns 0..100.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int ComputeQuality(ReadOnlySpan<float> buffer)
        {
            int gradientSum = 0;

            //
            // Vertical gradients.
            //
            for (int y = 0; y < DownsampleSize - 1; y++)
            {
                int row = y * DownsampleSize;
                int nextRow = row + DownsampleSize;

                for (int x = 0; x < DownsampleSize; x++)
                {
                    float u = buffer[row + x];
                    float v = buffer[nextRow + x];
                    int d = (int)(((u - v) * 100.0f) / 255.0f);
                    gradientSum += Math.Abs(d);
                }
            }

            //
            // Horizontal gradients.
            //
            for (int y = 0; y < DownsampleSize; y++)
            {
                int row = y * DownsampleSize;

                for (int x = 0; x < DownsampleSize - 1; x++)
                {
                    float u = buffer[row + x];
                    float v = buffer[row + x + 1];
                    int d =(int)(((u - v) * 100.0f) / 255.0f);
                    gradientSum += Math.Abs(d);
                }
            }

            //
            // Reference heuristic scaling.
            //
            int quality = gradientSum / 90;

            return quality > 100 ? 100 : quality;
        }

        private static void Dct64To16(ReadOnlySpan<float> input, Span<float> temp, Span<float> output)
        {
            //
            // B = D * A * D^T
            //
            // A: 64x64
            // D: 16x64
            // T: 16x64
            // B: 16x16
            //

            //
            // T = D * A
            //
            for (int i = 0; i < DctSize; i++)
            {
                int dctRow = i * DownsampleSize;

                int tempRow = i * DownsampleSize;

                for (int j = 0; j < DownsampleSize; j++)
                {
                    float sum0 = 0;
                    float sum1 = 0;
                    float sum2 = 0;
                    float sum3 = 0;

                    //
                    // Four accumulators reduce the dependency
                    // chain and tend to generate better code.
                    //
                    for (int k = 0; k < DownsampleSize; k += 4)
                    {
                        sum0 += DctMatrix[dctRow + k] * input[(k * DownsampleSize) + j];
                        sum1 += DctMatrix[dctRow + k + 1] * input[((k + 1) * DownsampleSize) + j];
                        sum2 += DctMatrix[dctRow + k + 2] * input[((k + 2) * DownsampleSize) + j];
                        sum3 += DctMatrix[dctRow + k + 3] * input[((k + 3) * DownsampleSize) + j];
                    }

                    temp[tempRow + j] =  sum0 + sum1 + sum2 + sum3;
                }
            }

            //
            // B = T * D^T
            //
            for (int i = 0; i < DctSize; i++)
            {
                int tempRow = i * DownsampleSize;

                int outputRow = i * DctSize;

                for (int j = 0; j < DctSize; j++)
                {
                    int dctRow = j * DownsampleSize;

                    float sum0 = 0;
                    float sum1 = 0;
                    float sum2 = 0;
                    float sum3 = 0;

                    for (int k = 0; k < DownsampleSize; k += 4)
                    {
                        sum0 += temp[tempRow + k] * DctMatrix[dctRow + k];
                        sum1 += temp[tempRow + k + 1] * DctMatrix[dctRow + k + 1];
                        sum2 += temp[tempRow + k + 2] * DctMatrix[dctRow + k + 2];
                        sum3 += temp[tempRow + k + 3] * DctMatrix[dctRow + k + 3];
                    }

                    output[outputRow + j] = sum0 + sum1 + sum2 + sum3;
                }
            }
        }

        private static PdqHash256 Quantize(ReadOnlySpan<float> dct)
        {
            Span<float> sorted =stackalloc float[DctSize * DctSize];
            dct.CopyTo(sorted);
            sorted.Sort();

            //
            // Lower median of 256 values.
            //
            float median = sorted[127];

            ulong w0 = 0;
            ulong w1 = 0;
            ulong w2 = 0;
            ulong w3 = 0;

            //
            // Bit N corresponds directly to
            // DCT coefficient N in row-major order.
            //
            for (int bit = 0; bit < 64; bit++)
            {
                if (dct[bit] > median)
                {
                    w0 |= 1UL << bit;
                }
            }

            for (int bit = 0; bit < 64;bit++)
            {
                if (dct[64 + bit] > median)
                {
                    w1 |= 1UL << bit;
                }
            }

            for (int bit = 0; bit < 64; bit++)
            {
                if (dct[128 + bit] > median)
                {
                    w2 |= 1UL << bit;
                }
            }

            for (int bit = 0; bit < 64; bit++)
            {
                if (dct[192 + bit] > median)
                {
                    w3 |= 1UL << bit;
                }
            }

            return new PdqHash256(w0,w1,w2,w3);
        }

        private static float[] CreateDctMatrix()
        {
            var matrix = new float[DctSize * DownsampleSize];
            float scale = MathF.Sqrt(2.0f / DownsampleSize);

            //
            // PDQ intentionally uses DCT frequencies
            // 1..16 rather than 0..15.
            //
            for (int i = 0; i < DctSize; i++)
            {
                for (int j = 0; j < DownsampleSize; j++)
                {
                    matrix[(i * DownsampleSize) + j] = scale * MathF.Cos((MathF.PI / 2.0f / DownsampleSize) * (i + 1) * ((2 * j) + 1));
                }
            }

            return matrix;
        }
    }
}
