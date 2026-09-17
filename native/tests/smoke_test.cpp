#include <gtest/gtest.h>

#include "core_info.h"

TEST(CoreInfo, VersionNotEmpty)
{
    ASSERT_NE(core_info_version(), nullptr);
    ASSERT_STRNE(core_info_version(), "");
}
