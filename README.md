<div align="center">

# CSNZ-Nar 解包打包工具

**一体化 NAR 归档解包 / 打包工具 · 流式处理 · 零参数自动还原**

[![Build](https://github.com/Fzhenhong/CSNZ-Nar/actions/workflows/release.yml/badge.svg)](https://github.com/Fzhenhong/CSNZ-Nar/actions/workflows/release.yml)
[![Release](https://img.shields.io/github/v/release/Fzhenhong/CSNZ-Nar?display_name=tag)](https://github.com/Fzhenhong/CSNZ-Nar/releases)
[![License](https://img.shields.io/github/license/Fzhenhong/CSNZ-Nar)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue)](https://github.com/Fzhenhong/CSNZ-Nar/releases)

By **大宏**

</div>

---

## 这个工具解决什么

CSNZ / CSOL 客户端把资源打包在 `.nar` 归档里。旧工具存在两个硬伤：

- **内存占用随文件线性增长**：整份归档读进内存，处理 2 GB 的 `cstrike.nar` 直接内存不足、解压中断。
- **解包与打包是两个独立程序**：中间还要人工判断归档根目录、存储方式、模型是否加密。

本工具重写了底层实现，并把这套判断变成自动。

## 核心特性

| 特性 | 说明 |
|---|---|
| **流式处理** | 内存占用与归档大小无关。实测 2 GB 归档峰值内存 **42 MB** |
| **多线程解包** | 按条目并行，读盘随机访问、写盘顺序，6 线程解完 1.2 GB 仅 5 秒 |
| **解包打包一体** | 同一个界面 / 同一条命令族，无需在两套工具间切换 |
| **零参数自动还原** | 解包时自动记录元信息，打包时自动套用，不需要人工指定任何参数 |
| **模型自动加解密** | 解包时自动解密 `.mdl`，打包时自动加密回原始版本号（20/21） |
| **容错** | 单文件失败不影响其余，最后汇总报告；写入完成前不覆盖目标文件 |

## 快速开始

从 [Releases](https://github.com/Fzhenhong/CSNZ-Nar/releases) 下载 `CSNZ-Nar-win-x64.zip`，解压后：

| 文件 | 说明 |
|---|---|
| `CSNZ-Nar.exe` | 图形界面，双击即用 |
| `nar.exe` | 命令行版本 |

**无需安装 .NET 运行时**（自包含发布）。

### 图形界面

窗口分「解包 NAR」「打包 NAR」两个页签，两边都只需选目录：

1. **解包**：拖入 `.nar` → 自动填好输出目录 → 点「开始解包」
2. **打包**：选源目录 → 点「开始打包」

归档根路径、存储方式、模型加密状态**全部自动还原**。

### 命令行

```bat
nar list    <归档.nar>                      列出条目
nar extract <归档.nar> <输出目录> [选项]     解包
nar pack    <输入目录> <输出.nar> [选项]      打包
```

常规用法就是零参数：

```bat
nar extract data\cstrike.nar 输出目录
nar pack 输出目录 新的.nar
```

完整参数见 [使用说明](docs/使用说明.md)。

## 自动还原机制

解包时在输出目录写出 `.nar-info.json`，逐条记录：

- 归档内完整路径 → 打包不必猜 `--root`
- 原始存储方式（raw / enc / enc+lz）→ 不必猜 `--type`
- 模型原本是否加密、用的是 20 还是 21 号密钥 → 能加密回原样
- 数据区偏移与目录表序号 → 还原归档布局

打包时自动向上查找清单并逐个套用。只有**新加的文件**才回退到默认设置。

## 实测数据

### 往返一致性

| 归档 | 规模 | 结果 |
|---|---|---|
| `cstrike.nar` | 2 GB / 10853 项 | 解包→打包后 **SHA-256 与原文件完全相同** |
| `fixtrike.nar` | 1.47 GB / 10996 项 | 10996/10996 项内容、路径、加密状态全一致 |
| `valve.nar` | 83 MB / 1244 项 | 1244/1244 项内容全一致 |

### 与原厂库兼容性

用官方 `CSOToolLib` 验证本工具生成的归档：

| 检查项 | 结果 |
|---|---|
| 原厂库打开 | 10996 条全部成功 |
| 原厂库全量解出 | 成功 10996 / 失败 0 |
| 原厂 `Verify()` CRC 校验 | 通过 10996 / 失败 0 |
| 解出内容 MD5 逐条比对 | 10996/10996 完全一致 |

### 性能

| 项目 | 结果 |
|---|---|
| 2 GB 归档解包峰值内存 | **42 MB** |
| 1.2 GB 解包（6 线程，含 CRC） | 5.4 秒 |
| 2 GB 打包 | 约 14 秒 |

## 格式参考

```
[0..8)              签名 "NAR\0" + 版本 0x01000000
[8 .. 数据区末)      各条目存储数据，偏移与长度由目录表给出
目录表              明文 → BZip2 压缩 → 按 16 字节表异或
尾部 8 字节          压缩长度 ^ 0x4076551F + 签名
```

三种存储类型：

| 值 | 名称 | 存储形式 |
|---|---|---|
| 0 | 原始 | 明文原样 |
| 1 | 编码 | 明文异或 16 字节路径密钥 |
| 2 | 编码+压缩 | `XOR(LZ77(明文))` |

细节（密钥派生、LZ77 线段格式、CRC 语义）见 [使用说明](docs/使用说明.md)。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bat
git clone https://github.com/Fzhenhong/CSNZ-Nar.git
cd CSNZ-Nar
dotnet build CSNZ-Nar.sln -c Release
```

发布单文件可执行：

```bat
dotnet publish src/NarGui/NarGui.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o artifacts/gui
```

打 tag 即触发 CI 自动构建并发布：

```bat
git tag v1.0.0 && git push origin v1.0.0
```

## 项目结构

```
src/
  NarCore/     核心库：格式解析、LZ 编解码、加解密、并发解包/打包
  NarCli/      命令行前端
  NarGui/      WinForms 前端
docs/          使用说明
.github/       CI/CD（构建 + Release）
```

## 已知边界

部分官方归档的数据区里残留着"已删除条目"的碎片（如 `fixtrike.nar` 有 50 处、约 292 MB）。
这些字节不属于任何目录表记录，无法从归档中还原。

因此这类归档重打包后**体积会变小**，整文件 SHA-256 与原文件不同。但已经过原厂库全量
CRC 校验与逐条内容比对确认：**所有条目的内容与客户端行为完全一致**，可直接替换。

## 许可

[MIT](LICENSE) © 2026 大宏
