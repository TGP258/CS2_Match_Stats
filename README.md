# CS2 对局统计

<!-- badges -->
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-v1.0.368-green.svg)](https://github.com/roflmuffin/CounterStrikeSharp)

[English Version (README_EN.md)](README_EN.md)

<!-- toc -->
## 目录

- [简介](#简介)
- [功能特点](#功能特点)
- [前置要求](#前置要求)
- [安装步骤](#安装步骤)
- [使用方法](#使用方法)
- [游戏内命令](#游戏内命令)
- [数据格式](#数据格式)
- [技术栈](#技术栈)
- [常见问题](#常见问题)
- [已知问题（待修复）](#已知问题待修复)
- [项目结构](#项目结构)
- [Credits](#credits)
- [许可证](#许可证)

---

## 简介

CS2 对局统计是一款 Counter-Strike 2 对局统计记录与网页查看工具。它可以自动记录对局数据，并提供美观的网页界面来查看对局历史和详细的玩家统计数据。

> **注意：** 本插件是 [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 项目的扩展插件。

---

## 功能特点

| 功能 | 说明 |
|------|------|
| 自动记录 | 监听游戏事件，自动记录每局数据 |
| 玩家统计 | K/D/A（击杀/死亡/助攻）、MVP、分数、Rating |
| 机器人支持 | 同时记录人类玩家和 BOT 数据 |
| 多地图支持 | 支持多种地图，中英文名称显示 |
| 事件记录 | 炸弹安放/拆除、爆头、武器 |
| 回合详情 | 点击回合圆点查看击杀详情弹窗 |
| 武器名称 | 中英文武器名称映射（手枪、冲锋枪、步枪、狙击枪等） |
| 队伍颜色 | TEAM A（蓝色）和 TEAM B（红色）颜色对应 |
| 换边检测 | 自动检测换边并正确计算比分 |
| 网页查看 | 通过浏览器访问对局历史 |
| 响应式设计 | 支持桌面和移动设备 |
| 双语支持 | 中文/英文界面切换 |

---

## 前置要求

安装本插件前，请确保已具备以下条件：

1. 已安装 **CS2-Bot-Improver** 主项目
2. **CounterStrikeSharp** 框架已正确安装
3. **.NET 8.0 SDK**（非必要，可选用已编译好的DLL）
4. **Node.js 16+**（用于运行网页查看器）

---

## 安装步骤

### 步骤 1：编译插件

```bash
cd CS2MatchStats
dotnet build -c Release
```

编译成功后，DLL 位于：`bin/Release/net8.0/CS2MatchStats.dll`
也可使用已编译好的插件[此处下载](https://github.com/TGP258/CS2_Match_Stats/releases)
### 步骤 2：安装插件到 CS2-Bot-Improver

将编译好的整个 `CS2MatchStats` 文件夹复制到 CS2-Bot-Improver 项目的 plugins 目录下：

```
CS2-Bot-Improver/
└── addons/
    └── counterstrikesharp/
        └── plugins/
            └── CS2MatchStats/     ← 复制到此处
                ├── CS2MatchStats.dll
                └── ...
```

### 步骤 3：安装网页查看器

将 `web` 文件夹复制到 Steam 目录：

```
SteamLibrary/
└── steamapps/
    └── common/
        └── Counter-Strike Global Offensive/
            └── web/              ← 复制到此处
                ├── package.json
                ├── server.js
                └── public/
```

### 步骤 4：启动网页查看器

```bash
cd "SteamLibrary/steamapps/common/Counter-Strike Global Offensive/web"
npm install
npm start
```

### 步骤 5：启动游戏

选择以下方式之一启动：

**方式一：使用 -insecure 参数启动**
- 在 Steam 中设置 CS2 启动选项：`-insecure`

**方式二：使用 CS2-Bot-Improver 的 Panel 启动**
- 通过 CS2-Bot-Improver 项目提供的 Panel 界面启动游戏

---

## 使用方法

1. 使用 `npm start` 启动网页服务器
2. 打开浏览器访问：`http://localhost:5173`
3. 启动游戏并进行对局
4. 对局结束后（切换地图或手动保存），数据会自动保存
5. 在网页中刷新即可查看对局记录

对局记录保存在：

```
SteamLibrary/steamapps/common/Counter-Strike Global Offensive/game/csgo/match_history/
```

---

## 游戏内命令

| 命令 | 说明 |
|------|------|
| `match_save` | 手动保存当前对局 |
| `match_list` | 列出已保存的对局 |
| `match_info` | 显示当前对局信息 |

---

## 数据格式

每局对局保存为 JSON 文件，包含：

```json
{
  "MapName": "de_dust2",
  "StartTime": "2024-01-01T12:00:00",
  "EndTime": "2024-01-01T12:15:30",
  "Duration": 930,
  "Teams": {
    "CT": {
      "Name": "Counter-Terrorists",
      "Score": 16,
      "Players": { ... }
    },
    "T": {
      "Name": "Terrorists",
      "Score": 12,
      "Players": { ... }
    }
  },
  "Rounds": [
    {
      "RoundNumber": 1,
      "Winner": "CT",
      "Reason": 2,
      "Events": [ ... ]
    }
  ]
}
```

### 玩家数据结构

```json
{
  "UniqueId": 12345,
  "Name": "玩家名称",
  "IsBot": false,
  "TeamKey": "CT",
  "InitialTeam": "CT",
  "Kills": 15,
  "Deaths": 5,
  "Assists": 3,
  "Score": 30,
  "MVPs": 2,
  "Rating": 1.25,
  "Headshots": 8,
  "FirstKills": 3,
  "MultiKills": 2,
  "Clutches": 1,
  "Trades": 4,
  "RoundsSurvived": 10,
  "TotalDamageDealt": 1850
}
```

---

## 技术栈

| 层级 | 技术 |
|------|------|
| 插件 | C# (.NET 8) + CounterStrikeSharp |
| 后端 | Node.js + Express |
| 前端 | 原生 HTML/CSS/JavaScript |

---

## 常见问题

### Q：为什么前端显示空页面？

**A：** 请确认：
1. 网页服务器已启动（`npm start`）
2. 对局记录目录存在且包含 JSON 文件

### Q：插件无法加载？

**A：** 请确认：
1. CS2-Bot-Improver 主项目已正确安装
2. CounterStrikeSharp 框架正常运行
3. 游戏以 `-insecure` 参数或通过 Panel 启动

### Q：玩家统计数据为空？

**A：** 如果对局中只有机器人且没有发生击杀事件，可能没有足够的数据。请进行正常游戏后再查看。

---

## 已知问题（待修复）

| 问题 | 说明 |
|------|------|
| 主控人机数据归属 | 若玩家使用 Bot 控制插件主控人机，该人机的击杀/死亡数据会算到主控玩家身上 |
| 误杀队友计数 | 杀害队友的行为目前也会被计入击杀数，需过滤同队击杀事件 |

---

## 项目结构

```
CS2MatchStats/
├── CS2MatchStats.csproj        # C# 项目配置
├── CS2MatchStatsPlugin.cs      # 插件主代码 (CounterStrikeSharp)
├── bin/                        # 编译输出（编译后生成）
│   └── Release/
│       └── net8.0/
│           └── CS2MatchStats.dll
└── web/                        # 网页查看器
    ├── package.json            # Node.js 依赖
    ├── server.js               # Express API 服务器
    └── public/
        └── index.html           # 前端页面 (HLTV 风格)
```

---

## Credits

本项目基于以下开源项目开发：

| 项目 | 说明 |
|------|------|
| [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) | 原项目，提供 CounterStrikeSharp 框架集成 |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | CS2 服务器插件框架 |

特别感谢所有为 CounterStrikeSharp 和 CS2-Bot-Improver 项目做出贡献的开发者。

---

## 许可证

本项目基于 CS2-Bot-Improver 开发，遵循原项目许可证。
