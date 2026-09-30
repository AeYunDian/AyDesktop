# AyDesktop

基于 WPF 的 Windows 桌面替代层。

AyDesktop 用一层透明窗口覆盖在系统桌面之上，把图标管理、文件夹分组、快捷启动整合成一个轻量的"第二桌面"。点击悬浮球即可随时呼出，不用时可以完全隐去，与原生桌面无缝切换。

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)](#系统要求)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#系统要求)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

---

## 功能特性

### 桌面

- **透明覆盖层**：全屏覆盖在系统桌面上，展示图标与背景
- **多文件夹同步**：默认监听用户桌面 + 公共桌面，可添加任意文件夹
- **实时监控**：文件增删改自动同步到界面
- **图标自由拖拽**：支持单选、多选（Ctrl / Shift）、框选、批量移动
- **对齐网格**：可配置网格大小与吸附，拖拽时显示预览

### 文件夹

- **两种样式**：全屏窗口 / 桌面分组框
- **每个文件夹独立配置**：右键 → 文件夹样式 → 三选一
- **拖拽合并**：两个图标叠在一起时自动创建文件夹
- **解压到桌面**：一键把文件夹内图标散回桌面

### 交互

- **悬浮球**：可拖动、半透明、可置顶，位置持久化
- **原生 Shell 右键菜单**：完整的系统右键菜单，包括 7-Zip、Git、TortoiseSVN 等所有 Shell 扩展
- **快捷方式箭头 / UAC 盾牌**：与系统一致的图标 overlay
- **进出动画**：图标容器从右向左滑入，可关闭
- **焦点感知**：切换到其他程序时自动隐藏，回到自身窗口时保持可见

### 账号

- **Ay 账号登录**：基于 OAuth 2.0 授权码流程
- **离线模式**：无网络时可跳过登录使用
- **会话加密**：AES-256-GCM 加密本地会话，密钥派生自硬件指纹（PBKDF2-SHA256, 20 万轮）
- **自动续期**：后台定时巡检并刷新 token

### 设置

- 分组标签页：**外观 / 行为 / 显示 / 文件夹 / 高级**
- 热生效：修改后无需重启
- 危险操作双重确认

---

## 系统要求

| 项目     | 要求                               |
| -------- | ---------------------------------- |
| 操作系统 | Windows 10 1809 (17763) 或更高版本 |
| 运行时   | .NET 8 Desktop Runtime             |
| 架构     | x64 / ARM64                        |

---

## 快速开始

### 从发布包安装

1. 从 [Releases](https://github.com/AeYunDian/AyDesktop/releases) 下载最新版
2. 解压到任意目录
3. 双击 `AyDesktop.exe` 启动

首次启动会弹出登录窗口，可选择登录 Ay 账号或进入离线模式。

### 从源码构建

```bash
git clone https://github.com/AeYunDian/AyDesktop.git
cd AyDesktop
cp .env.example .env
# 编辑 .env，填入 AYDESKTOP_CLIENT_SECRET_B32
dotnet build
dotnet run --project AyDesktop
```

发布：

```bash
dotnet publish AyDesktop -c Release
```

产物在 `AyDesktop/bin/Release/net8.0-windows/publish/`，只包含 `.exe` 和 `.dll`，无需携带额外配置。

---

## 配置

敏感配置通过 `.env` 文件提供，构建时编译为 C#代码。

### `.env`

```ini
AYDESKTOP_CLIENT_SECRET_B32=你的_BASE32_SECRET
```

### `.env.local`（可选，覆盖 `.env`）

用于本地开发时覆盖。两个文件都被 `.gitignore` 忽略，**不要提交到仓库**。

参考 `.env.example` 模板。

### 运行时行为

- 修改 `.env` 后需**重新构建**才生效
- 嵌入方式：`<EmbeddedResource>` + `LogicalName=AyDesktop.EnvData`

---

## 操作说明

| 操作                | 说明                               |
| ------------------- | ---------------------------------- |
| 单击悬浮球          | 切换桌面显示 / 隐藏                |
| 拖动悬浮球          | 移动位置，自动持久化               |
| 双击图标            | 打开文件 / 文件夹                  |
| 拖拽图标            | 移动，重叠时提示合并               |
| Ctrl / Shift + 单击 | 多选                               |
| 空白处拖动          | 框选                               |
| 右键图标            | 原生 Shell 菜单                    |
| 右键桌面            | 刷新、自动排列、新建文件夹、设置等 |
| ESC                 | 隐藏桌面 / 关闭文件夹窗口          |
| Ctrl + A            | 全选                               |
| Delete              | 删除选中项（移入回收站）           |

---

## 项目结构

```
AyDesktop/
├── AyDesktop/                  # 主程序
│   ├── Models/                 # 数据模型
│   │   ├── AppConfig.cs        # 应用配置（XML 序列化）
│   │   └── DesktopItem.cs      # 桌面项（树形结构，支持嵌套文件夹）
│   ├── ViewModels/
│   │   └── DesktopViewModel.cs # 核心视图模型 + 布局算法
│   ├── Views/                  # WPF 窗口与模板
│   │   ├── FloatWindow         # 悬浮球
│   │   ├── DesktopWindow       # 桌面覆盖层
│   │   ├── FolderWindow        # 全屏文件夹
│   │   ├── LoginWindow         # 登录
│   │   ├── SettingsWindow      # 设置
│   │   └── AboutWindow         # 关于
│   ├── Services/               # 服务层
│   │   ├── AuthService         # OAuth 会话管理
│   │   ├── ConfigService       # XML 配置读写
│   │   ├── IconExtractor       # Shell 图标提取（含 overlay 合成）
│   │   ├── ShellContextMenu    # 原生右键菜单
│   │   ├── DesktopMonitorService # 文件监控
│   │   ├── MachineKeyStore     # AES-GCM 本地加密
│   │   ├── HardwareId          # WMI 硬件指纹
│   │   └── ...                 # AdminHelper / StartupHelper 等
│   └── AyOAuthClientSDK.cs     # Ay OAuth SDK
├── .env.example                # 配置模板
├── .gitignore
└── LICENSE
```

---

## 开发

### 环境

- Visual Studio 2026
- .NET 8 SDK
- Windows 10 企业版 LTSC 21H2

### 调试

```bash
dotnet run --project AyDesktop
```

### 依赖

| 包                                           | 用途             |
| -------------------------------------------- | ---------------- |
| `System.Management`                          | WMI 硬件指纹采集 |
| `System.Security.Cryptography.ProtectedData` | DPAPI            |

依赖包均为 MIT 许可。

---

## 数据目录

所有运行时数据存放在 exe 同目录下的 `database/`：

```
database/
├── config.xml        # 应用配置
├── desktop.xml       # 图标布局与文件夹
├── account.bin       # 加密的登录会话（AES-GCM）
└── offline.bin       # 离线模式标志
```

首次运行自动创建。设置 → 高级 → 危险操作可清除。

---

## 致谢

- [.NET](https://dotnet.microsoft.com/) 与 [WPF](https://github.com/dotnet/wpf) 提供的桌面框架
- Windows Shell API 文档与社区经验
- 所有 [贡献者](https://github.com/AeYunDian/AyDesktop/graphs/contributors)

---

## 许可

本项目采用 **GNU General Public License v3.0** 许可。你可以自由使用、修改、分发，衍生作品需同样以 GPL-3.0 开源。

详见 [LICENSE](LICENSE)。

---

## 反馈

- 提交 Bug 或建议：[Issues](https://github.com/AeYunDian/AyDesktop/issues)
- 功能讨论：[Discussions](https://github.com/AeYunDian/AyDesktop/discussions)
