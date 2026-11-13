# 版本历史

每次发布都会在这里记录：**版本号 / 发布时间 / 更新内容 / 安装包下载地址**。
客户端启动时会请求目录服务的最新版本，据此强制更新到下面表里的最新版。

> 在线版本页（自动生成，始终与服务器一致）：<http://123.57.94.141:8787/versions>
> GitHub Releases：<https://github.com/as851500663/DllSmith/releases>

---

## v1.0.1 `2026-11-13` · 最新 · 强制更新

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

- 本站：<http://123.57.94.141:8787/files/DllSmith-Setup-1.0.1.exe>（2.7 MB）
- GitHub：<https://github.com/as851500663/DllSmith/releases/download/v1.0.1/DllSmith-Setup-1.0.1.exe>
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

- 本站：<http://123.57.94.141:8787/files/DllSmith-Setup-1.0.0.exe>（2.7 MB）
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
