# 本地普通聊天入口（0.1.49）

在单窗口Host & Play中，本地玩家主动发送的普通公开聊天默认进入现有Agent，无需每次写`!ai`。
旧前缀保持兼容，`!ai 查询状态`和`查询状态`得到同一任务文本。
普通问候只对话；只有明确授权时才执行游戏行动。任何新本地普通聊天都会取消正在执行的旧任务，
例如直接说`停止`可以中断探索。取消、等待Lua清理和拒绝旧回复的机制不变。

客户端原生outgoing hook仍先发送原消息一次，只捕获未消费的SayChatCommand。
不从显示回调猜测输入，不转发其他玩家/系统消息/AI作者广播或缓存回放；原Suppress机制仍排除自身发送回显。
斜杠命令、team/emote等非Say命令不进入Agent。消息保留原发送身份和事件ID，
协议内部source仍为local_ai_command，避免改动原分发和身份校验。

新增握手能力local_ai_chat_v2表示无需前缀。旧v1保留供旧Python兼容；新版Python启动必须看到v2，
旧客户端会明确提示需要0.1.49并退出，不会假装普通聊天已接通。文本仍限制2000个UTF-16字符，
拒绝空白、换行和NUL；只去掉首尾空白及可选的精确`!ai`入口前缀，不改正文。

## 本机启动

本次需要加载新Mod，不能只重启Python。保存退出旧游戏并停止旧Agent后，在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .local/start-client.ps1
```

快捷入口已指向新准备的0.1.49 profile `companion-g82-host-20261027-175538-078`，
从上一测试profile已经保存的角色/世界复制，旧profile保留不动；不含旧进程未保存的内存进度。
选择Multiplayer → Host & Play → mio → test，进入世界后另开终端：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/start-server-ai-agent.ps1
```

可直接说`你好`、`你周围有树吗`、`开始探索附近，遇到危险就停下`、`停止`。
新环境的包构建/新profile生成方式见[探索文档](AUTONOMOUS_EXPLORATION_MVP.md)。

## 定向验证与待验收

未运行全量测试。正式包0.1.49构建0错误/0警告；C#聊天34项及BridgeOnly相关组通过，
验证普通文本/中文/边界/可选前缀、真实TCP、普通新消息撤销旧回复、斜杠命令不发事件和自身发送抑制。
43项正式包IL检查通过，包括原生Say命令/IsConsumed过滤；原IPC/动作/读取/聊天定向协议组通过。
Python服务器Agent/探索Agent共18项通过，另增加旧客户端启动拒绝1项通过；
覆盖普通问候无游戏动作、原完整聊天执行/取消/作者路径和其他来源不触发。

本次没有启动图形客户端、没有调用外部模型；原生客户端捕获行为以源码/包IL和TCP替身验证，
仍需用户确认普通聊天能触发、AI回复不自循环、新普通聊天能取消探索，游戏命令不会触发Agent。
