# SP02：ARM32 UWP 只能由 VS2017 v141 提供（VS2026 v142/v145 无 ARM32，见 doc/ENV.md）
set(VCPKG_TARGET_ARCHITECTURE arm)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_CMAKE_SYSTEM_NAME WindowsStore)
# 必须钉到 19041：10.0 会让 CMake 选最新 SDK（26100），而 >=22621 的 SDK 已删除 um/arm，
# 链接测试程序时找不到 WindowsApp.lib（LNK1104）。19041 是最后一个含 ARM32 um lib 的 SDK。
set(VCPKG_CMAKE_SYSTEM_VERSION 10.0.19041.0)
set(VCPKG_PLATFORM_TOOLSET v141)
set(VCPKG_VISUAL_STUDIO_PATH "C:\\Program Files (x86)\\Microsoft Visual Studio\\2017\\Community")
# vcpkg 注入的 LIB 取自最新 SDK（26100，无 um/arm），CMAKE_SYSTEM_VERSION 不影响它。
# 给链接测试显式补 19041 的 arm 库目录，否则探测阶段 LNK1104 找不到 WindowsApp.lib。
# 必须用 8.3 短路径：VCPKG_LINKER_FLAGS 经 CMake -D 传递时内嵌引号会被剥掉，含空格路径会被截断。
# （8.3 名称为本机生成：C:\Progra~2\WI3CF2~1 = "C:\Program Files (x86)\Windows Kits"，见 native/NATIVE-BUILD.md）
set(VCPKG_LINKER_FLAGS "/LIBPATH:C:/PROGRA~2/WI3CF2~1/10/Lib/100190~1.0/um/arm /LIBPATH:C:/PROGRA~2/WI3CF2~1/10/Lib/100190~1.0/ucrt/arm")
