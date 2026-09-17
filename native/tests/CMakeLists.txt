# X06：宿主机 native 测试门禁。编译 native/core 为 ssh_core_host 静态库 + GoogleTest 冒烟。
# 构建：cmake -S native/tests -B native/tests/build -A x64 && cmake --build ... --config Release && ctest
cmake_minimum_required(VERSION 3.20)
project(ssh_core_host_tests CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 源码为 UTF-8（无 BOM），MSVC 默认按 ANSI(936) 解读会报 C4819 甚至吞字符
if(MSVC)
    add_compile_options(/utf-8)
endif()

include(FetchContent)
FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.14.0.zip
    URL_HASH SHA256=1f357c27ca988c3f7c6b4bf68a9395005ac6761f034046e9dde0896e3aba00e4
    DOWNLOAD_EXTRACT_TIMESTAMP OFF)
# 与宿主机默认 /MD 对齐（x64-windows-static OpenSSL 是 /MT，N01 接入时再统一，见 NATIVE-BUILD.md §1）
set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

enable_testing()

add_library(ssh_core_host STATIC
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/core_info.cpp)
target_include_directories(ssh_core_host PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/../core)

add_executable(smoke_test smoke_test.cpp)
target_link_libraries(smoke_test PRIVATE ssh_core_host GTest::gtest_main)

include(GoogleTest)
gtest_discover_tests(smoke_test)
