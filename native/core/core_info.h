#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// native 核心占位：N01 起逐步迁入 libssh2/libvterm/argon2 封装的纯 C 核心。
const char* core_info_version();

#ifdef __cplusplus
}
#endif
