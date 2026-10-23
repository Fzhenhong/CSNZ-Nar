# Release 正文

本目录存放每个版本的 GitHub Release 正文，文件名即 tag 名（如 `v1.1.2.md`）。

## 为什么放在仓库里

GitHub Release 的正文默认只渲染一个模板，看不到本版实际改了什么。
把正文纳入版本管理可以：

- 让每个 Release 页面直接显示真实更新内容
- 正文与代码同仓，改动能被 review 和追溯

## 发布新版本时

1. 在本目录新增 `vX.Y.Z.md`，写清本版改动
2. 同步在根目录 `CHANGELOG.md` 追加对应条目
3. `git tag vX.Y.Z && git push origin vX.Y.Z`

CI 会自动把 `release-notes/vX.Y.Z.md` 的内容作为 Release 正文。
