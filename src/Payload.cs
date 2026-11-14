// ============================================================================
//  4DDiG DLL Fixer — 重建工程
//  文件：Payload.cs  OSS 下发的 <name>_encrypt 载荷解密
//  依据：逆向报告 §8.3。实测结论：
//        远端对象 = [16 字节 IV][AES-128-CBC 密文]，key = "DDIG4SECUREKEY16"，
//        无填充（密文长度天然 16 对齐）。
//
//  1.4.0 t21（数据损坏级修复）：
//    · 远端并不全是 PE：SystemDll_win10_64 里 17 个 .msc 明文是 `<?xml`、gm.dls 是 `RIFF`。
//      旧实现判不出格式就"删明文、留密文"并 return false，而调用方不看返回值继续覆盖 →
//      把 AES 密文写进 System32，文件损坏。
//    · 现在：头校验失败**返回 UnknownFormat**（绝不"保留密文继续用"），调用方必须中止该项；
//    · 白名单按**目标扩展名**放行：.msc/.xml 允许 `<?xml`，.dls 允许 `RIFF`，其它未知格式一律拒写；
//    · 结果用 PayloadResult 表达，避免 bool 二义性（"没解密" vs "解密失败"）。
// ============================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DllFix.Core
{
    /// <summary>Normalize 的结果（替代原来的 bool，避免"没解密"和"解密失败"混在一起）</summary>
    public enum PayloadResult
    {
        AlreadyPlain,     // 本来就是明文（PE / 7z / zip / OLE / 白名单里的 XML/RIFF）
        Decrypted,        // 解出了明文并已就地替换
        NotEncryptable,   // 尺寸不像信封（不是 16 对齐的 IV+CT），且也不是明文 -> 不可用
        UnknownFormat,    // 解出来了，但头既不是 PE/压缩包，也不在目标扩展名白名单里 -> 必须中止该项
        Failed            // 解密过程抛异常
    }

    public static class Payload
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("DDIG4SECUREKEY16");

        /// <summary>
        /// 判断文件是否已经是"可落盘的明文"。
        /// 兼容旧调用：目标路径未知时按文件自身扩展名判断。
        /// </summary>
        public static bool LooksPlain(string path)
        {
            return LooksPlain(path, path);
        }

        /// <summary>
        /// 判断文件是否是可落盘的明文；`<?xml` 与 `RIFF` 只在目标扩展名白名单内放行
        /// （.msc 收 XML，.dls 收 RIFF；.xml/.wav 已在 t23 移除），其它未知格式一律 false。
        /// </summary>
        public static bool LooksPlain(string path, string targetPath)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[8];
                    int n = fs.Read(h, 0, h.Length);
                    if (n < 2) return false;

                    // 通用明文格式：PE / 7z / zip / OLE(MSI)
                    if (h[0] == 0x4D && h[1] == 0x5A) return true;                 // MZ
                    if (h[0] == 0x37 && h[1] == 0x7A) return true;                 // 7z
                    if (h[0] == 0x50 && h[1] == 0x4B) return true;                 // zip
                    if (h[0] == 0xD0 && h[1] == 0xCF) return true;                 // MSI (OLE)

                    string ext = TargetExtension(targetPath);

                    // XML：只认 .msc（MMC 管理单元）。.xml 已从 PathGuard 目标白名单移除（t23），
                    // 这里若继续放行就是死分支、且会让人误读为"还可以写 .xml"（t24 W1）。
                    if (h[0] == (byte)'<' && h[1] == (byte)'?'
                        && n >= 5
                        && h[2] == (byte)'x' && h[3] == (byte)'m' && h[4] == (byte)'l')
                    {
                        return ext == ".msc";
                    }

                    // RIFF：只认 .dls（DirectMusic 音色库）。
                    // 已移除 .wav：PathGuard 的目标白名单不放行 .wav，留着就是死分支（t23）。
                    if (h[0] == (byte)'R' && h[1] == (byte)'I'
                        && h[2] == (byte)'F' && h[3] == (byte)'F')
                    {
                        return ext == ".dls";
                    }

                    return false;
                }
            }
            catch { return false; }
        }

        private static string TargetExtension(string targetPath)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath)) return "";
                return Path.GetExtension(targetPath).ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>兼容旧签名：目标路径未知时按文件自身扩展名判断</summary>
        public static bool Normalize(string path)
        {
            return Normalize(path, path) == PayloadResult.Decrypted
                || Normalize(path, path) == PayloadResult.AlreadyPlain;
        }

        /// <summary>
        /// 若文件是 _encrypt 载荷则原地解密（原文件另存 .enc 便于回溯）。
        /// targetPath 用于白名单判定（XML/RIFF 只在对应扩展名目标上被接受）。
        /// **头校验失败时返回 UnknownFormat，且不把密文当成可用明文**——调用方必须中止该项。
        /// </summary>
        public static PayloadResult Normalize(string path, string targetPath)
        {
            if (!File.Exists(path)) return PayloadResult.Failed;
            if (LooksPlain(path, targetPath)) return PayloadResult.AlreadyPlain;

            FileInfo fi = new FileInfo(path);
            if (fi.Length <= 32 || (fi.Length - 16) % 16 != 0)
            {
                Log.Info("payload not decryptable (size " + fi.Length + "): " + Path.GetFileName(path));
                return PayloadResult.NotEncryptable;
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

                // 头校验：不在白名单就删掉明文并**明确失败**（绝不"保留密文继续用"）
                if (!LooksPlain(tmp, targetPath))
                {
                    try { File.Delete(tmp); } catch { }
                    Log.Error("decrypt produced unknown format（已中止，未落盘）: "
                              + Path.GetFileName(path) + " target=" + Path.GetFileName(targetPath ?? ""), null);
                    return PayloadResult.UnknownFormat;
                }

                string backup = path + ".enc";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
                File.Move(tmp, path);
                Log.Info("decrypted payload: " + Path.GetFileName(path));
                return PayloadResult.Decrypted;
            }
            catch (Exception ex)
            {
                Log.Error("payload decrypt failed: " + Path.GetFileName(path), ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return PayloadResult.Failed;
            }
        }

        /// <summary>自检辅助：按信封格式加密（IV 随机 + AES-128-CBC/NoPadding），仅供 --payload-selftest 使用</summary>
        public static void SealForSelfTest(string plainPath, string outPath)
        {
            byte[] plain = File.ReadAllBytes(plainPath);
            int pad = (16 - (plain.Length % 16)) % 16;
            if (pad > 0)
            {
                byte[] padded = new byte[plain.Length + pad];
                Array.Copy(plain, padded, plain.Length);
                plain = padded;
            }
            using (Aes aes = Aes.Create())
            {
                aes.Key = Key;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (FileStream fs = File.Create(outPath))
                {
                    fs.Write(aes.IV, 0, 16);
                    using (CryptoStream cs = new CryptoStream(fs, aes.CreateEncryptor(), CryptoStreamMode.Write))
                        cs.Write(plain, 0, plain.Length);
                }
            }
        }
    }
}
