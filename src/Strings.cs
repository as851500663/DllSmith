// ============================================================================
//  DllSmith 1.4 — 界面文案集中表（i18n 资源结构）
//  文件：Strings.cs
//  目的（商用就绪项）：把界面可见文案从各处硬编码集中到一处，方便后续做
//        中/英切换与整体校对。当前默认 zh-CN；切换语言只需换一张表。
//  用法：Str.T(Str.MainTitle) / Str.F(Str.ScanProgress, 12, 100)（格式化版本）。
//  说明：1.4.0 已把主壳、导航、页面标题、按钮、空状态、关键对话框文案接入本表；
//        ScanPage 行模板里的少量列标题仍在 XAML 字符串内（见 fixes-client-1.4.md 说明）。
// ============================================================================

using System;

namespace DllFix.Ui
{
    public static class Str
    {
        // ── 主壳 ────────────────────────────────────────────────────────────
        public const string MainTitle = "电脑系统修复与 DLL 修复工具";
        public const string MainSubtitle = "一键检测并修复系统运行库、DirectX、系统 DLL 等常见问题，\n让电脑运行更稳定、更流畅。";
        public const string Ready = "就绪";
        public const string VisitSite = "访问官网  ↗";
        public const string GitHubProject = "GitHub 开源项目";

        // ── 导航 ────────────────────────────────────────────────────────────
        public const string NavOverview = "全面体检";
        public const string NavRuntime = "运行库修复";
        public const string NavDirectX = "DirectX 修复";
        public const string NavSystemDll = "系统 DLL 修复";
        public const string NavDownLevel = "系统文件修复";
        public const string NavDownload = "系统文件下载";
        public const string NavClean = "C 盘清理";

        // ── 页面描述 ────────────────────────────────────────────────────────
        public const string DescRuntime = "检测并修复 Microsoft Visual C++ 运行库，解决程序无法启动、缺少 DLL 等问题。";
        public const string DescDirectX = "修复 DirectX 组件缺失或损坏导致的游戏报错、画面异常问题。";
        public const string DescSystemDll = "解决运行游戏或打开应用时提示 DLL 文件缺失或损坏的问题。";
        public const string DescDownLevel = "修复 DownLevel API 集（api-ms-win-*）缺失，解决旧程序在新系统上无法启动的问题。";
        public const string DescDownload = "从镜像源下载系统文件与运行库安装包（仅下载，不安装）。";

        // ── 按钮 / 空状态 ───────────────────────────────────────────────────
        public const string BtnScan = "开始检查";
        public const string BtnRescan = "重新检查";
        public const string BtnRepair = "一键修复";
        public const string BtnDownloadOnly = "一键下载";
        public const string BtnStop = "停止";
        public const string EmptyScan = "尚未检查：点击右上角「开始检查」开始检测这一类。";

        // ── 状态 / 进度（带占位符）──────────────────────────────────────────
        public const string StatusScanDone = "扫描完成：共 {0} 项，缺失/待修复 {1} 项";
        public const string StatusScanDoneReadOnly = "扫描完成：共 {0} 项，缺失/待修复 {1} 项（只读：目录未通过完整性校验）";
        public const string StatusChecking = "检测中 {0}/{1}，缺失 {2}";
        public const string StatusManifest = "正在校验目录签名…（{0} 条）";
        public const string StatusLoadCatalog = "正在加载目录…";

        // ── 修复相关（文案里必须体现"签名/HTTPS/覆盖率"）────────────────────
        public const string RepairConfirmTitle = "确认修复";
        public const string RepairCoverage = "目录完整性：已通过签名校验（HTTPS）。\n哈希覆盖 {0} / {1}";
        public const string RepairUncovered = "\n注意：{0} 项内容未做哈希校验（目录已签名、走 HTTPS）。";
        public const string RepairDeadSkipped = "\n其中 {0} 项「源缺失」已跳过（暂不支持自动修复）。";
        public const string LabelSourceDead = "源缺失";
        public const string DetailSourceDead = "源缺失，暂不支持自动修复";
        public const string DetailDriver = "驱动文件，不在修复范围";
        public const string LabelHashUncovered = "哈希未覆盖";
        public const string ReadOnlyBanner = "目录完整性校验失败，已切换为只读：";

        // ── 更新（发布门禁文案）──────────────────────────────────────────────
        public const string UpdateRejectedTitle = "更新记录未通过校验";
        public const string UpdateRejectedBody = "为保证安全，本次更新已中止：不会下载、不会运行任何文件。\n详情见 Logs\\ 目录。";
        public const string UpdateMandatoryTitle = "必须更新";
        public const string UpdateMandatoryBody = "当前版本已停止支持，需更新后继续使用。";
        public const string UpdateNow = "立即更新";
        public const string UpdateExit = "退出程序";
        public const string UpdateOptionalTitle = "发现新版本";

        // ── 回滚 / 校验 / 权限（t17 评审新增）────────────────────────────────
        public const string BtnRollback = "还原被替换的文件";
        public const string RollbackHint = "覆盖系统文件前会自动留档 .rebuild.bak，可在这里一键还原。";
        public const string ColumnCheck = "校验";
        public const string ColumnProgress = "进度";
        public const string HashUncovered = "哈希未覆盖";
        public const string HashChecked = "已校验";
        public const string RecheckForTrust = "请重新检查以校验目录签名";
        public const string NeedAdminTitle = "需要管理员权限";
        public const string NeedAdminCleanBody = "清理系统缓存（Windows\\Temp、更新缓存、日志等）需要管理员权限。\n\n确定 = 以管理员身份重新启动\n取消 = 只清理当前用户可写的项（跳过 {0} 项）";
        public const string FreedZero = "没有可释放的内容：文件可能正被占用、已在上次清理过，或当前权限不足。\n未被删除的文件不会影响正常使用。";
        public const string RepairFailedTitle = "部分条目修复失败";
        public const string RepairFailedBody = "失败 {0} 项，前几条原因如下（完整明细见执行日志）：\n\n{1}";

        // ── 语言表切换点 ────────────────────────────────────────────────────
        public static string T(string s)
        {
            return s;
        }

        public static string F(string template, params object[] args)
        {
            try { return string.Format(template, args); }
            catch { return template; }
        }
    }
}
