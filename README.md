# ClipboardPro · 剪贴板历史（重启不丢失）

一个 Windows 剪贴板历史管理工具，复刻系统自带 Win+V 面板的外观与操作习惯，但**历史记录落盘保存、重启不清除、条数上限可自定义**。

## 为什么需要它

Windows 自带的剪贴板历史（Win+V）有两个硬限制，且没有任何开关可以改变：

- **重启即清空**：历史记录由内存态服务（cbdhsvc）承载，重启后除固定项外全部丢失；
- **上限固定 25 条**：写死在程序里，注册表与组策略均无法调整。

本项目用约两千行 C#/WPF 自研实现同样的面板体验，把历史持久化到本地磁盘。

## 功能特性

- **持久化**：历史写入 `history.json`（原子写入，异常退出不丢数据），图片存 `blobs/` 目录；
- **条数自定义**：默认保留 500 条，可设 0（不限）到 99999；超出自动淘汰最旧的未固定条目；
- **接管 Win+V**：通过低层键盘钩子（WH_KEYBOARD_LL）屏蔽系统自带面板，把 Win+V 留给本应用；可在设置中关闭，恢复系统原生行为；
- **界面复刻系统**：单列卡片、浅色描边、右上「…」/右下图钉、悬停高亮，支持浅色/深色/跟随系统主题；
- **支持文本 / 图片 / 文件**三种内容，内容指纹去重、重复复制自动置顶；
- **固定（📌）**：固定条目永不淘汰、跨重启保留；
- **可拖动**：按住面板顶部空白处拖到任意位置，自动记忆；
- **失焦不消失**：仅点 ✕ / Esc / 再次按 Win+V 关闭；
- **数据目录可自定义**：设置中一键迁移（含历史与图片），自动重启生效；
- **隐私保护**：尊重密码管理器写入的「不纳入剪贴板历史」标记（`ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`），敏感内容不入库；
- **托盘小猫**（可在设置中隐藏托盘图标）。

## 安装与使用

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)；
2. 构建并发布：

   ```bash
   git clone https://github.com/joozattn/clipboard-pro.git
   cd clipboard-pro
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
   ```

3. 运行 `publish/ClipboardPro.exe`，托盘出现小猫图标即常驻后台（已自动注册开机自启）；
4. 按 `Win+V` 唤出面板。

> 备用热键 `Ctrl+Alt+V` 始终可用（在 Win+V 接管关闭或异常时兜底）。

## 配置

配置文件位于数据目录（默认 `%APPDATA%\ClipboardPro\config.json`，自定义目录时随数据迁移）：

| 字段 | 默认值 | 说明 |
|---|---|---|
| `MaxEntries` | `500` | 历史保存条数上限，`0` = 不限制；固定项不计入 |
| `InterceptWinV` | `true` | 接管 Win+V（屏蔽系统剪贴板面板） |
| `RunAtStartup` | `true` | 开机自动启动 |
| `ShowTrayIcon` | `true` | 显示托盘图标 |
| `PasteOnSelect` | `true` | 选中条目后自动粘贴到原窗口 |
| `Theme` | `Auto` | `Auto` / `Light` / `Dark` |
| `DataDirOverride` | 空 | 自定义数据目录 |
| `MaxBlobBytes` | 32 MB | 单条图片上限，超出跳过 |

## 技术要点

- **Win+V 接管**：`RegisterHotKey` 无法抢占系统热键（错误码 1409），改用 `WH_KEYBOARD_LL` 低层钩子。关键在**完全吞掉 Win 键按下/抬起并按需补发成对事件**——只吞抬起会让系统认为 Win 键常按，导致后续按键全部变成 `Win+字母` 组合误触发系统窗口；补发事件用 `dwExtraInfo` 私有标记放行，规避 `SendInput` 异步时序下被自身钩子再吞。
- **钩子线程模型**：低层键盘钩子回调必须瞬时返回，否则会阻塞整个系统键盘输入。钩子安装在一独立线程（自建 `GetMessage` 消息循环），回调内只做轻量状态机。
- **剪贴板监听**：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE`。WPF 剪贴板 API 只能在 STA 线程调用，监听窗口与读取均运行在专用 STA 线程（线程池是 MTA，异常会被吞掉导致"静默不捕获"）。
- **持久化**：索引 JSON 原子写入（临时文件 + 覆盖移动），图片以 PNG 存 `blobs/`，索引只留引用；启动时校验 blob 存在性，损坏自动备份重建。

## 已知限制

- 接管 Win+V 依赖钩子常驻：应用退出后系统 Win+V 自动恢复原生行为；
- 全屏独占模式游戏中，普通窗口可能被游戏覆盖；
- 仅支持 Windows 10/11（.NET 8 WPF）。

## 构建

```bash
dotnet build -c Release
```

## License

MIT
