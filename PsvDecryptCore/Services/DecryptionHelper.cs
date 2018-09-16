using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PsvDecryptCore.Services
{
    /// <summary>
    ///     Provides a helper class to decrypt the incoming video feed.
    /// </summary>
    public static class DecryptionHelper
    {
        internal const string KeyOne = "plu" +
                                       "ral" +
                                       "sight";

        internal const string KeyTwo =
            "\x0006?zY\x00a2\x00b2\x0085\x009fL\x00be\x00ee0" +
            "\x00d6.\x00ec\x0017#\x00a9>\x00c5\x00a3Q\x0005\x00a4\x00b0\x0001" +
            "8\x00de^\x008e\x00fa\x0019Lq\x00df'\x009d\x0003\x00dfE\x009eM\x0080" +
            "'x:\0~\x00b9\x0001\x00ff 4\x00b3\x00f5\x0003\x00c3\x00a7\x00ca\x000e" +
            "A\x00cb\x00bc\x0090\x00e8\x009e\x00ee~\x008b\x009a\x00e2\x001b\x00b8" +
            "UD<\x007fK\x00e7*\x001d\x00f6\x00e67H\v\x0015Ar\x00fd*v\x00f7" +
            "%\x00c2\x00fe\x00be\x00e4;p\x00fc";

        /// <summary>
        ///     Decrypts the input file to the specified output.
        /// </summary>
        /// <param name="input">Full file path including the filename for the input.</param>
        /// <param name="output">Full file path including the filename for the output.</param>
        /// <param name="token">Cancellation token used to stop the decryption process.</param>
        /// <returns>
        ///     A task that represents the asynchronous decryption process.
        /// </returns>
        public static async Task DecryptFileAsync(string input, string output, CancellationToken token = default)
        {
            using (var inputStream = File.OpenRead(input))
            using (var outputStream = File.OpenWrite(output))
            {
                var buffer = new Memory<byte>(new byte[inputStream.Length]);
                inputStream.Seek(0, SeekOrigin.Begin);
                await inputStream.ReadAsync(buffer, token).ConfigureAwait(false);
                for (var index = 0; index < inputStream.Length; index++)
                {
                    var num = (byte) ((ulong) (KeyOne[index % KeyOne.Length] ^
                                               KeyTwo[index % KeyTwo.Length]) ^
                                      (ulong) (index % 251L));
                    buffer.Span[index] = (byte) (buffer.Span[index] ^ num);
                }

                await outputStream.WriteAsync(buffer, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Gets the required module hash for course directory name.
        /// </summary>
        public static string GetModuleHash(string name, string authorHandle)
        {
            using (var md5 = MD5.Create())
            {
                return Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(name + "|" + authorHandle)))
                    .Replace('/', '_');
            }
        }
    }
}