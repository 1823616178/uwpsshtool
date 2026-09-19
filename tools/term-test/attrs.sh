#!/usr/bin/env sh
# T06 属性探针。在 SSH 交互 shell 里执行：  sh attrs.sh
# 对照：bold / italic / underline / strike / dim / reverse / invisible /
# ANSI 0–15、bold-as-bright、中文与 emoji 宽字符对齐。
#
# 注：libvterm 0.3.3 屏幕层没有 dim 属性，SGR 2 预期看起来与 normal 相同。

printf 'normal text\n'
printf '\033[1mbold\033[0m\n'
printf '\033[3mitalic\033[0m\n'
printf '\033[4munderline\033[0m\n'
printf '\033[9mstrikethrough\033[0m\n'
printf '\033[2mdim (libvterm 无 dim，预期同 normal)\033[0m\n'
printf '\033[7mreverse\033[0m\n'
printf 'invisible[\033[8mHIDDEN\033[0m]after\n'
printf '\nANSI 0-7: '
printf '\033[30m 0 \033[31m 1 \033[32m 2 \033[33m 3 \033[34m 4 \033[35m 5 \033[36m 6 \033[37m 7 \033[0m\n'
printf 'ANSI 8-15: '
printf '\033[90m 8 \033[91m 9 \033[92m10 \033[93m11 \033[94m12 \033[95m13 \033[96m14 \033[97m15 \033[0m\n'
printf 'bold-as-bright (1;31 vs 31): '
printf '\033[31mred\033[0m \033[1;31mbold-red\033[0m\n'
printf 'wide CJK: A中B文C  emoji: A😀B\n'
printf 'mix: \033[1;4;31mbold+underline+red\033[0m \033[7;32mreverse+green\033[0m\n'
