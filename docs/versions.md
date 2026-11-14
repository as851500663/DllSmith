# 版本历史

每次发布都会在这里记录：**版本号 / 发布时间 / 更新内容 / 安装包下载地址**。
客户端启动时会请求目录服务的最新版本，据此强制更新到下面表里的最新版。

> 所有安装包与更新说明统一发布在 [Releases](../../releases)。

---

## v1.4.1 `2026-11-14` · 最新 · 强制更新

**修复**
- 导航页映射错位：`系统文件修复 / 系统文件下载 / C 盘清理` 三页对应的页面被交换（点 C 盘清理会进入下载页），已在 `ShowPage` 与总览 `pageIndex` 两侧对齐
- 菜单命名歧义：`系统文件修复` → **系统 API 修复**（DownLevel `api-ms-win-*`），`系统文件下载` → **离线包下载**（只下载不安装），两者职责不再混淆

**包含 1.4.0 的全部改动**：更新链路 RSA-3072 签名 + 强制 HTTPS、签名 manifest + 逐字节哈希、非 PE 载荷写入修复、扩展名白名单收敛、空闲 CPU <1%、服务端加固。

SHA256：`d38d71980ef7735b24d216cfcf352f481485b515c22a34df210db4ffbcf1016e`

---

## v1.4.0 `2026-11-14` · 最新 · 强制更新

**安全（发布硬门禁）**
- 更新链路改为 **RSA-3072 / RSASSA-PKCS1-v1_5 / SHA-256** 签名 + **强制 HTTPS**（内置根 CA 模数 pinning），验签或哈希失败立即中止
- 修复链路改为**签名 manifest + 逐字节 SHA256**：目录被篡改或条目不在签名清单内时整页只读
- 修复 18 条非 PE 载荷（17 个 `.msc` 为 `<?xml`、`gm.dls` 为 `RIFF`）会被写成密文覆盖系统文件的问题
- 目标扩展名白名单收敛为 `.dll .msc .dls .mui .cpl .com .rll`，非 `.dll` 覆盖写审计日志

**体验与性能**
- 空闲 CPU 67.7%（单核）→ **<1%**；启动 522ms → **336ms**；离线扫描等待 12.3s → ≤1.5s
- 「还原被替换的文件」回滚入口、哈希未覆盖角标、未提权清理提示、修复失败原因直达界面
- 列表虚拟化 Recycling + Pixel，3350 行只实例化 12 个容器

**服务端**
- 路径穿越/CSRF/登录限流（IP+账号复合键）/Cookie 属性/请求体上限/非 root systemd/每日备份（7 份，含完整性校验）
- 新增 9443 HTTPS 入口、签名 manifest 与 `/api/v1/oss-url`、目录哈希覆盖 3230/15172

**下载**：[Releases · v1.4.0](../../releases/tag/v1.4.0) · SHA256 `de077b27e866804d13f866a40f47f7abb68269cde571a55b797b35a3732036e1`

---

## v1.1.0 `2026-11-14` · 最新 · 强制更新

**新增：C 盘清理**

- 缓存清理：临时文件 / Windows 更新缓存 / 传递优化 / 预读取 / 系统日志 / 崩溃转储 / 缩略图缓存 / Edge、Chrome 缓存 / 回收站
- 大文件夹迁移：`%LOCALAPPDATA%`、`%APPDATA%` 下的大目录搬到其他盘，自动创建 Junction，失败自动回滚
- 回收站默认不勾选（清空不可恢复）；迁移写回滚清单 `Logs\clean-*.json`
- 统计卡：C 盘可用 / 可清理合计 / 已选择

**下载**：见 [Releases](../../releases/tag/v1.1.0)

---

## v1.0.3 `2026-11-13` · 强制更新

**界面**

- 列表排序：缺失项永远置顶，正常项在下，未判定垫底
- 执行日志默认收起为一行（显示最近一条），点「展开」看完整输出
- 移除右下角百分比与进度条，改为状态栏内「小圆圈 + 进度百分比」
- 按钮禁用态改为浅底深字（原来灰底白字看不清）

**更新**

- 点「确定」后弹出下载进度窗：百分比、进度条、已下载 / 总大小，下载完成才启动安装包
- 下载改成分块读取，进度回调更稳定

**下载**：见 [Releases](../../releases/tag/v1.0.3)

---

## v1.0.2 `2026-11-13` · 强制更新

**新增**

- 客户端顶部新增「访问官网 ↗」按钮，直接打开下载与版本页
- 版本信息区文案整理，图标与按钮细节统一

**下载**：见 [Releases](../../releases/tag/v1.0.2)

---

## v1.0.1 `2026-11-13` · 强制更新

**修复**

- 一键修复会把“正常”的组件也纳入修复集合：
  - 目录服务导出条目时丢了 `FILEPATH` / `MATCH` / `DOWNLOADDLL` 三个字段，客户端拿不到路径 → 运行库、DirectX、DownLevel 条目被整体误判为缺失（实测误判 542/3324）
  - 客户端不认 `MATCH` 通配字段，改为「WinSxS 目录索引」一次性判定，不再逐条全盘 `GetFiles`
  - 修复集合从 `State != Present` 收紧为 `State == Absent`，未检测(Unknown)/已修复(Done) 不再进入修复队列
- 安装向导不再先弹「选择安装语言」

**改进**

- 「一键修复」执行前二次确认：`将修复 N 项缺失组件，自动跳过 M 项正常组件`
- 更新包带 SHA256 校验，下载后校验不一致直接报错退出
- 服务端发布记录同版本号自动覆盖，不再重复堆积

**下载**

- [Releases · v1.0.1](../../releases/tag/v1.0.1)
- SHA256：`c74b663bb58c32cb58874eea283d1911003ba5275af510a240bb9bcd63a5ba45`

---

## v1.0.0 `2026-11-13` · 历史版本

**首个可运行版本**

- 五个功能页：全面体检 / 运行库修复 / DirectX 修复 / 系统 DLL 修复 / 系统文件下载
- 运行库（VC++ 2005–2022）、DirectX、DownLevel、系统 DLL 的完整性识别
- 修复动作：静默安装 / 7z 解压 / 单文件覆盖（WOW64 重定向 + `*.rebuild.bak` 备份）
- 目录服务对接、强制更新、在线心跳统计、日志面板

**已知问题**

- 目录服务导出缺 `FILEPATH` / `MATCH` 字段，导致运行库条目被误判为缺失（v1.0.1 修复）

**下载**

- [Releases · v1.0.0](../../releases/tag/v1.0.1)（历史版本请见 Releases 列表）
- SHA256：`d7d7421ca4347cbb…`

---

## 发布流程（维护者）

```bash
# 1) 改版本号并编译
#    src/Core.cs      -> AppInfo.Version
#    installer/*.iss  -> #define AppVersion
powershell -File .\build.ps1
& "C:\Program Files\Inno Setup 7\ISCC.exe" .\installer\DllSmith.iss

# 2) 上传安装包并在目录服务里登记该版本
scp dist/DllSmith-Setup-X.Y.Z.exe root@your-server:/opt/dllsmith/files/


# 3) GitHub 打 tag + Release（附带安装包）
git tag vX.Y.Z && git push origin vX.Y.Z
gh release create vX.Y.Z dist/DllSmith-Setup-X.Y.Z.exe --title "DllSmith X.Y.Z" --notes "…"

# 4) 更新本文件与 README 的版本表
```
