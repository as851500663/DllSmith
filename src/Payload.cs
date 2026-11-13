// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：Payload.cs  OSS 下发的 <name>_encrypt 载荷解密
//  依据：逆向报告 §8.3。实测结论（2026-11-13 验证）：
//        远端对象 = [16 字节 IV][AES-128-CBC 密文]，key = "DDIG4SECUREKEY16"，
//        无填充（密文长度天然 16 对齐），解密后首字节为 MZ / 7z / PK。
// ============================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DllFix.Core
{
    public static class Payload
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("DDIG4SECUREKEY16");

        /// <summary>判断文件是否已经是明文（PE / 7z / zip / MSI(OLE)）</summary>
        public static bool LooksPlain(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[8];
                    int n = fs.Read(h, 0, 8);
                    if (n < 2) return false;
                    if (h[0] == 0x4D && h[1] == 0x5A) return true;                 // MZ
                    if (h[0] == 0x37 && h[1] == 0x7A) return true;                 // 7z
                    if (h[0] == 0x50 && h[1] == 0x4B) return true;                 // zip
                    if (h[0] == 0xD0 && h[1] == 0xCF) return true;                 // MSI (OLE)
                    return false;
                }
            }
            catch { return true; }
        }

        /// <summary>
        /// 若文件是 _encrypt 载荷则原地解密（原文件另存 .enc 便于回溯）；
        /// 返回 true 表示做过解密。
        /// </summary>
        public static bool Normalize(string path)
        {
            if (!File.Exists(path)) return false;
            if (LooksPlain(path)) return false;

            FileInfo fi = new FileInfo(path);
            if (fi.Length <= 32 || (fi.Length - 16) % 16 != 0)
            {
                Log.Info("payload not decryptable (size " + fi.Length + "): " + Path.GetFileName(path));
                return false;
            }

            string tmp = path + ".dec";
            try
            {
                using (FileStream input = File.OpenRead(path))
                {
                    byte[] iv = new byte[16];
                    int read = 0;
                    while (read < 16)
                    {
                        int n = input.Read(iv, read, 16 - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }

                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = Key;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.None;

                        using (CryptoStream cs = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        using (FileStream output = File.Create(tmp))
                        {
                            byte[] buf = new byte[1 << 20];
                            int n;
                            while ((n = cs.Read(buf, 0, buf.Length)) > 0)
                                output.Write(buf, 0, n);
                        }
                    }
                }

                if (!LooksPlain(tmp))
                {
                    File.Delete(tmp);
                    Log.Info("decrypt produced unknown format: " + Path.GetFileName(path));
                    return false;
                }

                string backup = path + ".enc";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
                File.Move(tmp, path);
                Log.Info("decrypted payload: " + Path.GetFileName(path));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("payload decrypt failed: " + Path.GetFileName(path), ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
