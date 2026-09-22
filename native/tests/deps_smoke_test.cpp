#include <vterm.h> // Must precede Windows headers: rpcndr.h defines the token `small` as a macro.
#include <argon2.h>
#include <libssh2.h>

#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <cstring>

TEST(DependencySmoke, Libssh2Version)
{
    ASSERT_EQ(0, libssh2_init(0));
    const char* version = libssh2_version(0);
    ASSERT_NE(nullptr, version);
    EXPECT_STREQ("1.11.1", version);
    libssh2_exit();
}

TEST(DependencySmoke, LibvtermCells)
{
    VTerm* terminal = vterm_new(24, 80);
    ASSERT_NE(nullptr, terminal);
    vterm_set_utf8(terminal, 1);

    VTermScreen* screen = vterm_obtain_screen(terminal);
    ASSERT_NE(nullptr, screen);
    vterm_screen_reset(screen, 1);
    ASSERT_EQ(2U, vterm_input_write(terminal, "hi", 2));
    vterm_screen_flush_damage(screen);

    VTermScreenCell first{};
    VTermScreenCell second{};
    EXPECT_EQ(1, vterm_screen_get_cell(screen, VTermPos{0, 0}, &first));
    EXPECT_EQ(1, vterm_screen_get_cell(screen, VTermPos{0, 1}, &second));
    EXPECT_EQ(static_cast<std::uint32_t>('h'), first.chars[0]);
    EXPECT_EQ(static_cast<std::uint32_t>('i'), second.chars[0]);

    vterm_free(terminal);
}

TEST(DependencySmoke, Argon2idRfc9106Section53)
{
    std::array<std::uint8_t, 32> output{};
    std::array<std::uint8_t, 32> password{};
    std::array<std::uint8_t, 16> salt{};
    std::array<std::uint8_t, 8> secret{};
    std::array<std::uint8_t, 12> associatedData{};
    password.fill(0x01);
    salt.fill(0x02);
    secret.fill(0x03);
    associatedData.fill(0x04);

    argon2_context context{};
    context.out = output.data();
    context.outlen = static_cast<std::uint32_t>(output.size());
    context.pwd = password.data();
    context.pwdlen = static_cast<std::uint32_t>(password.size());
    context.salt = salt.data();
    context.saltlen = static_cast<std::uint32_t>(salt.size());
    context.secret = secret.data();
    context.secretlen = static_cast<std::uint32_t>(secret.size());
    context.ad = associatedData.data();
    context.adlen = static_cast<std::uint32_t>(associatedData.size());
    context.t_cost = 3;
    context.m_cost = 32;
    context.lanes = 4;
    context.threads = 1;
    context.version = ARGON2_VERSION_13;
    context.allocate_cbk = nullptr;
    context.free_cbk = nullptr;
    context.flags = ARGON2_DEFAULT_FLAGS;

    ASSERT_EQ(ARGON2_OK, argon2_ctx(&context, Argon2_id));

    constexpr std::array<std::uint8_t, 32> expected{
        0x0d, 0x64, 0x0d, 0xf5, 0x8d, 0x78, 0x76, 0x6c,
        0x08, 0xc0, 0x37, 0xa3, 0x4a, 0x8b, 0x53, 0xc9,
        0xd0, 0x1e, 0xf0, 0x45, 0x2d, 0x75, 0xb6, 0x5e,
        0xb5, 0x25, 0x20, 0xe9, 0x6b, 0x01, 0xe6, 0x59,
    };
    EXPECT_EQ(0, std::memcmp(output.data(), expected.data(), output.size()));
}

TEST(DependencySmoke, SecurityConstantsAndBoundaryDefinitions)
{
    // Q11 security patch constants (CVE-2026-55200, CVE-2026-66034, CVE-2026-66035)
    EXPECT_EQ(40000, LIBSSH2_PACKET_MAXPAYLOAD);
    EXPECT_EQ(-41, LIBSSH2_ERROR_OUT_OF_BOUNDARY);
    EXPECT_EQ(-38, LIBSSH2_ERROR_BUFFER_TOO_SMALL);
}

