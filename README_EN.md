# CS2 Match Stats

<!-- badges -->
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-v1.0.368-green.svg)](https://github.com/roflmuffin/CounterStrikeSharp)

[中文版本 (README.md)](README.md)

<!-- toc -->
## Table of Contents

- [About](#about)
- [Features](#features)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Usage](#usage)
- [Game Commands](#game-commands)
- [Data Format](#data-format)
- [Tech Stack](#tech-stack)
- [FAQ](#faq)
- [Known Issues (To Be Fixed)](#known-issues-to-be-fixed)
- [Project Structure](#project-structure)
- [Credits](#credits)
- [License](#license)

---

## About

CS2 Match Stats is a Counter-Strike 2 match statistics recording and web viewing tool. It automatically records match data and provides a beautiful web interface to view match history with detailed player statistics.

> **Note:** This plugin is an extension of [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver).

---

## Features

| Feature | Description |
|---------|-------------|
| Auto Recording | Listens to game events and automatically records match data |
| Player Stats | K/D/A (Kills/Deaths/Assists), MVP, Score, Rating |
| Bot Support | Records both human players and BOT data |
| Multi-Map | Supports multiple maps with names in Chinese/English |
| Event Records | Bomb plant/defuse, headshots, weapons |
| Round Details | Click round dots to view kill details in popup |
| Weapon Names | Chinese/English weapon name mapping (pistols, SMGs, rifles, snipers, etc.) |
| Team Colors | TEAM A (blue) and TEAM B (red) color correspondence |
| Side Swap | Auto-detects side swap and calculates scores correctly |
| Web Viewer | View match history through browser |
| Responsive | Works on desktop and mobile devices |
| Bilingual | Chinese/English interface toggle |

---

## Prerequisites

Before installing this plugin, make sure you have:

1. **CS2-Bot-Improver** main project installed
2. **CounterStrikeSharp** framework properly installed
3. **.NET 8.0 SDK** (for compiling the plugin)
4. **Node.js 16+** (for running the web viewer)

---

## Installation

### Step 1: Build the Plugin

```bash
cd CS2MatchStats
dotnet build -c Release
```

The compiled DLL will be at: `bin/Release/net8.0/CS2MatchStats.dll`

### Step 2: Install Plugin to CS2-Bot-Improver

Copy the compiled `CS2MatchStats` folder to the CS2-Bot-Improver plugins directory:

```
CS2-Bot-Improver/
└── addons/
    └── counterstrikesharp/
        └── plugins/
            └── CS2MatchStats/     ← Copy here
                ├── CS2MatchStats.dll
                └── ...
```

### Step 3: Install Web Viewer

Copy the `web` folder to the Steam directory:

```
SteamLibrary/
└── steamapps/
    └── common/
        └── Counter-Strike Global Offensive/
            └── web/              ← Copy here
                ├── package.json
                ├── server.js
                └── public/
```

### Step 4: Start Web Viewer

```bash
cd "SteamLibrary/steamapps/common/Counter-Strike Global Offensive/web"
npm install
npm start
```

### Step 5: Launch Game

Choose one of the following methods:

**Method 1: Launch with `-insecure` parameter**
- Set CS2 launch options in Steam: `-insecure`

**Method 2: Launch via CS2-Bot-Improver Panel**
- Use the Panel interface provided by CS2-Bot-Improver

---

## Usage

1. Start the web server with `npm start`
2. Open browser and visit: `http://localhost:5173`
3. Start the game and play matches
4. After a match ends (map change or manual save), data is automatically saved
5. Refresh the web page to view match records

Match records are saved to:

```
SteamLibrary/steamapps/common/Counter-Strike Global Offensive/game/csgo/match_history/
```

---

## Game Commands

| Command | Description |
|---------|-------------|
| `match_save` | Manually save current match |
| `match_list` | List saved matches |
| `match_info` | Show current match info |

---

## Data Format

Each match is saved as a JSON file containing:

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

### Player Data Structure

```json
{
  "UniqueId": 12345,
  "Name": "PlayerName",
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

## Tech Stack

| Layer | Technology |
|-------|------------|
| Plugin | C# (.NET 8) + CounterStrikeSharp |
| Backend | Node.js + Express |
| Frontend | Pure HTML/CSS/JavaScript |

---

## FAQ

### Q: Why does the frontend show an empty page?

**A:** Please confirm:
1. Web server is running (`npm start`)
2. Match records directory exists and contains JSON files

### Q: Plugin won't load?

**A:** Please confirm:
1. CS2-Bot-Improver main project is properly installed
2. CounterStrikeSharp framework is working
3. Game is launched with `-insecure` parameter or via Panel

### Q: Player statistics are empty?

**A:** If the match only has bots and no kills occurred, there may not be enough data. Please play a normal match and check again.

---

## Known Issues (To Be Fixed)

| Issue | Description |
|-------|-------------|
| Bot Control Data Attribution | If a player uses a Bot control plugin to control a bot, that bot's kill/death data will be attributed to the controlling player |
| Team Kill Counting | Killing teammates is currently counted as kills, needs to filter same-team kill events |

---

## Project Structure

```
CS2MatchStats/
├── CS2MatchStats.csproj        # C# project config
├── CS2MatchStatsPlugin.cs      # Main plugin code (CounterStrikeSharp)
├── bin/                        # Build output (generated after build)
│   └── Release/
│       └── net8.0/
│           └── CS2MatchStats.dll
└── web/                        # Web viewer
    ├── package.json            # Node.js dependencies
    ├── server.js               # Express API server
    └── public/
        └── index.html          # Frontend page (HLTV style)
```

---

## Credits

This project is developed based on the following open source projects:

| Project | Description |
|---------|-------------|
| [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) | Original project, provides CounterStrikeSharp framework integration |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | CS2 server plugin framework |

Special thanks to all contributors of CounterStrikeSharp and CS2-Bot-Improver projects.

---

## License

This project is developed based on CS2-Bot-Improver and follows the original project's license.
