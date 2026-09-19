# X06：宿主机 native 测试门禁。编译 native/core 为 ssh_core_host 静态库 + GoogleTest 冒烟。
# 构建：cmake -S native/tests -B native/tests/build -A x64 && cmake --build ... --config Release && ctest
cmake_minimum_required(VERSION 3.20)
project(ssh_core_host_tests C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# SP02 的 x64-windows-static OpenSSL 使用静态 CRT；所有宿主目标（含 GTest）保持一致。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# 源码为 UTF-8（无 BOM），MSVC 默认按 ANSI(936) 解读会报 C4819 甚至吞字符
if(MSVC)
    add_compile_options(/utf-8)
endif()

include(FetchContent)
FetchContent_Declare(googletest
    URL https://github.com/google/googletest/archive/refs/tags/v1.14.0.zip
    URL_HASH SHA256=1f357c27ca988c3f7c6b4bf68a9395005ac6761f034046e9dde0896e3aba00e4
    DOWNLOAD_EXTRACT_TIMESTAMP OFF)
set(gtest_force_shared_crt OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

enable_testing()

add_library(ssh_core_host STATIC
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/core_info.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/io/EventLoop.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/io/SessionThread.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/io/WinsockInit.cpp)
target_include_directories(ssh_core_host PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/../core)
target_link_libraries(ssh_core_host PUBLIC ws2_32)

set(THIRD_PARTY_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../third_party)
set(LIBSSH2_DIR ${THIRD_PARTY_DIR}/libssh2)
set(LIBVTERM_DIR ${THIRD_PARTY_DIR}/libvterm)
set(ARGON2_DIR ${THIRD_PARTY_DIR}/argon2)
set(OPENSSL_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../prebuilt/x64-windows-static)

foreach(required_file
        ${LIBSSH2_DIR}/include/libssh2.h
        ${LIBVTERM_DIR}/src/encoding/DECdrawing.inc
        ${ARGON2_DIR}/include/argon2.h
        ${OPENSSL_DIR}/lib/libcrypto.lib)
    if(NOT EXISTS ${required_file})
        message(FATAL_ERROR "Missing ${required_file}. Run scripts/fetch-third-party.ps1 and scripts/build-openssl.ps1.")
    endif()
endforeach()

file(GLOB LIBSSH2_SOURCES CONFIGURE_DEPENDS ${LIBSSH2_DIR}/src/*.c)
list(FILTER LIBSSH2_SOURCES EXCLUDE REGEX "[/\\\\](agent_win|os400qc3)\\.c$")
file(GLOB LIBVTERM_SOURCES CONFIGURE_DEPENDS ${LIBVTERM_DIR}/src/*.c)
set(ARGON2_SOURCES
    ${ARGON2_DIR}/src/argon2.c
    ${ARGON2_DIR}/src/core.c
    ${ARGON2_DIR}/src/encoding.c
    ${ARGON2_DIR}/src/ref.c
    ${ARGON2_DIR}/src/thread.c
    ${ARGON2_DIR}/src/blake2/blake2b.c)

add_library(native_dependencies_host STATIC
    ${LIBSSH2_SOURCES}
    ${LIBVTERM_SOURCES}
    ${ARGON2_SOURCES})
target_compile_definitions(native_dependencies_host PRIVATE
    LIBSSH2_OPENSSL
    ARGON2_NO_THREADS
    _CRT_SECURE_NO_DEPRECATE
    _CRT_NONSTDC_NO_DEPRECATE
    _WINSOCK_DEPRECATED_NO_WARNINGS)
target_include_directories(native_dependencies_host PUBLIC
    ${LIBSSH2_DIR}/include
    ${LIBVTERM_DIR}/include
    ${ARGON2_DIR}/include
    PRIVATE
    ${LIBSSH2_DIR}/src
    ${LIBVTERM_DIR}/src
    ${ARGON2_DIR}/src
    ${ARGON2_DIR}/src/blake2
    ${OPENSSL_DIR}/include)
target_link_libraries(native_dependencies_host PUBLIC
    ${OPENSSL_DIR}/lib/libcrypto.lib
    crypt32
    ws2_32)

add_library(ssh_session_host STATIC
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/ssh/session.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/ssh/hostkey.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/ssh/auth.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/ssh/channel.cpp)
target_include_directories(ssh_session_host PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/../core)
# hostkey.cpp 用 OpenSSL EVP 算指纹；native_dependencies_host 的 OpenSSL include 是 PRIVATE，这里单独补。
target_include_directories(ssh_session_host PRIVATE ${OPENSSL_DIR}/include)
target_link_libraries(ssh_session_host PUBLIC ssh_core_host native_dependencies_host)

add_executable(smoke_test smoke_test.cpp)
target_link_libraries(smoke_test PRIVATE ssh_core_host GTest::gtest_main)

add_executable(deps_smoke_test deps_smoke_test.cpp)
target_link_libraries(deps_smoke_test PRIVATE native_dependencies_host GTest::gtest_main)

add_executable(io_test event_loop_test.cpp session_thread_test.cpp)
target_link_libraries(io_test PRIVATE ssh_core_host GTest::gtest_main)

add_executable(ssh_session_test ssh_session_test.cpp)
target_link_libraries(ssh_session_test PRIVATE ssh_session_host GTest::gtest_main)

add_executable(hostkey_test hostkey_test.cpp)
target_link_libraries(hostkey_test PRIVATE ssh_session_host GTest::gtest_main)

add_executable(auth_test auth_test.cpp)
target_link_libraries(auth_test PRIVATE ssh_session_host GTest::gtest_main)
# 测试内用绝对路径定位 fixtures/keys（ctest 工作目录是 build/）。
target_compile_definitions(auth_test PRIVATE SSH_TEST_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}")

add_executable(channel_test channel_test.cpp)
target_link_libraries(channel_test PRIVATE ssh_session_host GTest::gtest_main)
target_compile_definitions(channel_test PRIVATE SSH_TEST_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}")

add_executable(reconnect_policy_test reconnect_policy_test.cpp)
target_link_libraries(reconnect_policy_test PRIVATE ssh_session_host GTest::gtest_main)

add_executable(keepalive_test keepalive_test.cpp)
target_link_libraries(keepalive_test PRIVATE ssh_session_host GTest::gtest_main)
target_compile_definitions(keepalive_test PRIVATE SSH_TEST_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}")

add_executable(error_codes_test error_codes_test.cpp)
target_link_libraries(error_codes_test PRIVATE ssh_session_host GTest::gtest_main)

add_executable(bridge_logic_test bridge_logic_test.cpp)
target_link_libraries(bridge_logic_test PRIVATE ssh_core_host GTest::gtest_main)

add_library(ssh_term_host STATIC
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/term/grid.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/term/vterm_screen.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/term/scrollback.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/term/snapshot.cpp)
target_include_directories(ssh_term_host PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/../core
    PRIVATE ${LIBVTERM_DIR}/include)
target_link_libraries(ssh_term_host PUBLIC native_dependencies_host)

add_executable(term_grid_test term_grid_test.cpp)
target_link_libraries(term_grid_test PRIVATE ssh_term_host GTest::gtest_main)

add_executable(vterm_screen_test vterm_screen_test.cpp)
target_link_libraries(vterm_screen_test PRIVATE ssh_term_host GTest::gtest_main)

add_executable(scrollback_test scrollback_test.cpp)
target_link_libraries(scrollback_test PRIVATE ssh_term_host GTest::gtest_main)

add_executable(snapshot_test snapshot_test.cpp)
target_link_libraries(snapshot_test PRIVATE ssh_term_host GTest::gtest_main)

# S03：原生保险库密码学（Argon2 ref.c + OpenSSL；只追加，不动既有条目）。
add_library(ssh_crypto_host STATIC
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/crypto/aad.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../core/crypto/vault.cpp)
target_include_directories(ssh_crypto_host PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/../core
    PRIVATE ${OPENSSL_DIR}/include)
target_link_libraries(ssh_crypto_host PUBLIC native_dependencies_host)

add_executable(aad_test aad_test.cpp)
target_link_libraries(aad_test PRIVATE ssh_crypto_host GTest::gtest_main)

add_executable(vault_test vault_test.cpp)
target_link_libraries(vault_test PRIVATE ssh_crypto_host GTest::gtest_main)

add_executable(vault_ops_test vault_ops_test.cpp)
target_link_libraries(vault_ops_test PRIVATE ssh_crypto_host GTest::gtest_main)

include(GoogleTest)
gtest_discover_tests(smoke_test)
gtest_discover_tests(deps_smoke_test)
gtest_discover_tests(io_test)
gtest_discover_tests(ssh_session_test)
gtest_discover_tests(hostkey_test)
gtest_discover_tests(auth_test)
gtest_discover_tests(channel_test)
gtest_discover_tests(reconnect_policy_test)
gtest_discover_tests(keepalive_test)
gtest_discover_tests(error_codes_test)
gtest_discover_tests(bridge_logic_test)
gtest_discover_tests(term_grid_test)
gtest_discover_tests(vterm_screen_test)
gtest_discover_tests(scrollback_test)
gtest_discover_tests(snapshot_test)
gtest_discover_tests(aad_test)
gtest_discover_tests(vault_test)
gtest_discover_tests(vault_ops_test)
