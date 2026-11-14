// ============================================================================
//  DllSmith 1.4 — 目录联接（不再经 cmd /c mklink 拼接，安全审计 A-06）
//  文件：Junction.cs
//  说明：迁移后必须让原路径仍可访问。这里用 Win32 API 直接建立**目录符号链接**
//        （FileAttributes.ReparsePoint，IsJunction 判定一致），避免把用户目录名拼进
//        cmd 命令行（原实现 RunCmd("mklink /J \"" + src + "\" ...") 存在参数注入面）。
//        需要管理员权限（本工具迁移流程本来就要求提权）。
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DllFix.Core
{
    public static class Junction
    {
        private const int SYMBOLIC_LINK_FLAG_DIRECTORY = 0x1;
        private const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

        /// <summary>
        /// 在 link 位置建立指向 target 的目录联接。成功返回 null，失败返回可读原因。
        /// </summary>
        public static string Create(string link, string target)
        {
            if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(target))
                return "参数为空";
            if (Directory.Exists(link) || File.Exists(link))
                return "目标位置已被占用：" + link;
            if (!Directory.Exists(target))
                return "链接源目录不存在：" + target;

            try
            {
                // 先尝试普通目录符号链接；Win10 起允许非提权 + 显式标志
                bool ok = CreateSymbolicLink(link, target,
                    SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 4 = ERROR_TOO_MANY_LINKS 之类的兼容问题，去掉 unprivileged 标志重试一次
                    if (err == 87 || err == 1 || err == 1314)
                        ok = CreateSymbolicLink(link, target, SYMBOLIC_LINK_FLAG_DIRECTORY);
                    if (!ok)
                    {
                        int e2 = Marshal.GetLastWin32Error();
                        Log.Error("CreateSymbolicLink failed link=" + link + " target=" + target
                                  + " err=" + e2, null);
                        return "CreateSymbolicLink 失败（Win32 错误 " + e2 +
                               (e2 == 1314 ? "：需要管理员权限" : "") + "）";
                    }
                }
                Log.Info("junction created: " + link + " -> " + target);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("junction exception", ex);
                return ex.Message;
            }
        }

        /// <summary>删除联接本身（不动目标目录内容）</summary>
        public static bool Remove(string link)
        {
            try
            {
                if (!Directory.Exists(link)) return false;
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) return false;
                Directory.Delete(link, false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("junction remove failed: " + link, ex);
                return false;
            }
        }
    }
}
