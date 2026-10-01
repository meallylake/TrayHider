# TrayHider —— 把任何窗口收进系统托盘

任务栏或桌面上碍眼的窗口，一键收进系统托盘；双击托盘里它自己的图标，原样放回来。
`dist\TrayHider.exe` 是编译好的成品（GUI 子系统，双击不会闪控制台窗口，无第三方依赖）。

## 它做什么

- **收**：把**任何**正在运行的程序的主窗口（不只是本程序启动的）藏起来——窗口消失、任务栏按钮消失，进程照常运行。
- **放**：托盘里为每个被隐藏的窗口单独显示一个图标（图标取自那个程序自己的 exe），双击就放回来。
- **守**：有些程序会自己把窗口再弹出来，TrayHider 每秒检查一次再收回去；你手动放出来的，不会再被收走。
- **送**：能让一个程序"从头就不露脸"地启动，它的窗口一出现就被收走。

对被隐藏的程序**零侵入**：只用 `user32` 的 `ShowWindowAsync`，不注入、不挂钩子、不改对方任何东西。

## 三种用法

### 1. 随手收（最快）

| 按键 | 作用 |
| --- | --- |
| `Ctrl+Alt+H` | 把**当前前台窗口**收进托盘 |
| `Ctrl+Alt+Shift+H` | 把全部被隐藏的窗口放出来 |

### 2. 挑着收（管理窗口）

双击托盘主图标（或运行 `TrayHider.exe`）：

- **窗口**页：列出此刻所有可见的顶层窗口（标题 / 进程 / PID / 状态）。选中 → 「收进托盘」；属于托盘的选中 → 「放出来」；**双击一行**来回切换。
- **启动到托盘**页：选一个 exe / bat / cmd / lnk，填参数，点「启动并收进托盘」。勾「记住它」会写一条规则，以后这个程序一出现就自动收进托盘（重启也有效）。
- **规则与设置**页：删规则、每个窗口是否独立托盘图标、默认是否强制保持隐藏、快捷键开关、开机自启、数据目录。

关闭管理窗口不会退出程序（只是藏回托盘）；要彻底退出：右键托盘主图标 →「退出（并恢复全部窗口）」。

### 3. 命令行

```
TrayHider.exe                       打开管理窗口（并确保托盘常驻）
TrayHider.exe --hide-foreground     把当前前台窗口收进托盘
TrayHider.exe --hide <标题|PID>      把匹配的窗口收进托盘
TrayHider.exe --restore <标题|PID>   放出匹配的窗口
TrayHider.exe --restore-all         放出全部
TrayHider.exe --launch <exe> [--args "..."] [--keep]
                                    启动到托盘（--keep 强制保持隐藏）
TrayHider.exe --list [--out 文件]    列出当前可见窗口（便于脚本/排错）
TrayHider.exe --quit                退出托盘常驻（先恢复所有窗口）
--data-dir <目录>                   指定数据目录
--resident                          以常驻模式运行（开机自启用的就是它）
```

命令行只是把命令写进队列文件，由常驻实例在 250ms 内执行；常驻实例没在跑时会自动拉起。
所以 `TrayHider.exe --hide-foreground` 可以绑到任意启动器/快捷方式上。

## 三层"不会把你坑了"的安全网

1. **正常退出**（托盘菜单 / `--quit`）：先把所有被隐藏的窗口放回来，再退出。
2. **崩溃 / 被任务管理器强杀**：隐藏清单实时写在 `hidden.state` 里，下次启动 TrayHider 自动把那些窗口放回来（实测有效）。
3. **兜底**：托管异常与 `ProcessExit` 两个钩子都会再恢复一次。

## 已知边界（实测结论，不含糊）

- **管理员权限的窗口**：Windows 的 UIPI 会拦下跨权限的窗口操作。目标程序以管理员身份运行时，TrayHider 也必须以管理员启动，否则**藏不住**——这时它会在 6 秒后弹气泡明确告诉你，而不是默默失败。
- **隐藏 ≠ 挂起**：程序照常跑、照常占 CPU/内存/端口，只是看不见。要停掉它，在它的托盘图标上右键 →「结束该进程」。
- **新窗口**：被隐藏的程序之后自己弹出的新窗口（比如一个对话框），只有在规则或"启动到托盘"看护期（60 秒）内才会被自动收走。
- **整窗口粒度**：隐藏 Windows Terminal 那样的窗口，是整个终端窗口（含所有标签页）一起收起来。
- 全屏独占游戏、部分 UWP 窗口的行为可能不同；万一状态不对，右键托盘 →「恢复全部窗口」即可回退。

## 数据与配置

- 数据目录：默认 `%APPDATA%\TrayHider`；**exe 同目录放一个 `portable.txt` 就变成便携模式**，数据写到 `exe目录\data\`（`dist\` 里已经放了，所以现在这份是便携的）。
- `settings.ini`：快捷键、独立图标、默认保持隐藏、开机自启。
- `rules.txt`：自动收取规则，每行一条、制表符分隔。
- `trayhider.log`：运行日志，排错先看它。
- `hidden.state`：崩溃恢复用，正常退出会删掉。

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

- 用 Windows 自带的 `csc.exe`（.NET Framework 4.x）编译，**没有任何第三方依赖**；
- 源码在 `src\`（`Native.cs` 窗口 API / `Hider.cs` 收放引擎 / `Store.cs` 配置与队列 / `TrayApp.cs` 托盘宿主与命令行 / `ManagerForm.cs` 界面）；
- `build.ps1` 会自动生成图标并编译出 `dist\TrayHider.exe`。

## 测试

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1   # 功能端到端
powershell -ExecutionPolicy Bypass -File tests\ui-test.ps1     # 管理界面冒烟
```

`run-tests.ps1` 是真刀真枪的实证测试：先做对照实验（直接启动窗口确实可见），再真的隐藏、真的恢复，每一步都用 `EnumWindows` 枚举可见窗口来核对，还包含全局快捷键（真的按键）与崩溃恢复。跑完会在 `tests\` 下生成 `report.txt`、`ui-report.txt`（已在 `.gitignore` 里，不进仓库）。

两个脚本都用 `$PSScriptRoot` 定位项目，clone 到任何目录都能直接跑。

## 开发时踩到的坑（都固化进代码了）

1. **Windows PowerShell 5.1 按系统 ANSI 读没有 BOM 的 `.ps1`**：脚本里有中文就必须存成 UTF-8 **with BOM**，否则解析报错、脚本秒退（本项目的 `tests\*.ps1` 都带 BOM，`build.ps1` 干脆只写 ASCII）。
2. **csc 同理**：`.cs` 源码没有 BOM 时按 ANSI 读，中文字符串会乱码。编译时加 `/codepage:65001`（本项目已加），或给源码存 BOM。
3. **`/target:winexe`（GUI 子系统）才是"绝不闪窗口"的正解**：控制台子系统程序靠 `-WindowStyle Hidden` 隐藏，始终有被 Windows Terminal 接管、弹窗/弹标签页的风险（上一版 dsh 托盘就是靠 wscript 绕过这一点的）。
4. **`Process.Start(..., UseShellExecute=false)` 会让子进程继承父进程的标准句柄**：常驻子进程会一直握着管道，导致调用方在读取输出时被挂死（本次测试脚本就被挂了 10 分钟）。启动常驻进程一律用 `UseShellExecute=true`。
5. **别把函数命名为 `Cli`**：`cli` 是 PowerShell 内置别名（`Clear-Item`），别名优先级高于自定义函数，调用会被静默劫持。
6. **隐藏别人的窗口要挑**：只认"有标题、非 `WS_EX_TOOLWINDOW`、非 IME / GDI+ / `.NET-BroadcastEventWindow` 内部窗口"的那些，否则会把程序的隐形辅助窗口一起藏掉（早期版本真的踩了，日志里能看到）。
7. **`Process.Handle` 在 PowerShell + `Start-Process -PassThru` 下可能取不到值**，要按 PID 绑 Job Object 就用 `OpenProcess` 自己开句柄。
8. **显式恢复过的窗口要记账**：否则"启动看护"会在你刚放出来时立刻又收回去（本次测试第 5 阶段就抓到了这个 bug）。
9. **跨进程 `SendMessage` 不会封送指针**：像 `TCM_GETITEMRECT` 这种"参数是指针"的控件消息，跨进程发过去会让**目标进程**往它自己的地址空间里乱写——我第一版 UI 测试就是这么把 TrayHider 写崩的。要远程点控件，只用不带指针的消息（`WM_LBUTTONDOWN/UP`）。
10. **别给常驻的 GUI 实例设 `WindowStyle = Hidden`**：`STARTUPINFO` 里的 `SW_HIDE` 会被该进程的**第一个窗口**继承，管理窗口将永远显示不出来（同样实测踩过）。GUI 子系统程序本来就没有控制台窗口，不需要这一手。
11. **命令队列要么原子写、要么别删**：客户端"写文件 → 常驻实例读文件"的队列，必须"先写 `.tmp` 再改名"（否则读到半个文件），并且**读失败时绝不能删**（否则命令被静默吞掉——`--quit` 就这么失效过一次）。
12. **WinForms 的 `TabPage` 子控件是懒创建的**：只有标签页第一次可见时才建句柄，所以自动化测试要真的切换标签页才能验证另外两页。
13. **不要自己指定 `CreateParams.ClassName`**（比如图省事写 `"static"`）：那会污染 WinForms 的窗口类注册表，之后创建任何窗口都会抛 `类已存在`。

## 许可证

MIT，见 [LICENSE](LICENSE)。
