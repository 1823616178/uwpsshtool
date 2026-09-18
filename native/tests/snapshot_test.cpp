#include <gtest/gtest.h>

#include <cstring>
#include <vector>

#include "term/grid.h"
#include "term/scrollback.h"
#include "term/snapshot.h"

using sshclient::term::Cell;
using sshclient::term::CellGrid;
using sshclient::term::ScrollbackBuffer;
using sshclient::term::copyDirtyRows;
using sshclient::term::copyViewport;
using sshclient::term::dirtyBitmapByteSize;
using sshclient::term::getText;
using sshclient::term::gridByteSize;
using sshclient::term::kAttrSoftWrap;
using sshclient::term::kDefaultBgMarker;
using sshclient::term::kDefaultFgMarker;

namespace {

Cell makeCell(uint32_t cp, uint16_t attrs = 0)
{
    Cell c{};
    c.codepoint = cp;
    c.fgArgb = kDefaultFgMarker;
    c.bgArgb = kDefaultBgMarker;
    c.attrs = attrs;
    return c;
}

} // namespace

TEST(SnapshotTest, NoDirtyReturnsFalseAndLeavesBuffers)
{
    CellGrid g(4, 2, kDefaultFgMarker, kDefaultBgMarker);
    std::vector<uint8_t> rows(gridByteSize(4, 2), 0xAB);
    std::vector<uint8_t> dirty(dirtyBitmapByteSize(2), 0xCD);
    EXPECT_FALSE(copyDirtyRows(g, rows.data(), rows.size(), dirty.data(), dirty.size()));
    EXPECT_EQ(rows[0], 0xAB);
    EXPECT_EQ(dirty[0], 0xCD);
}

TEST(SnapshotTest, SizeMismatchFailsClosed)
{
    CellGrid g(4, 2, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'A'));
    std::vector<uint8_t> tiny(3, 0);
    std::vector<uint8_t> dirty(dirtyBitmapByteSize(2), 0);
    EXPECT_FALSE(copyDirtyRows(g, tiny.data(), tiny.size(), dirty.data(), dirty.size()));
    EXPECT_TRUE(g.isDirty(0)); // 失败不清脏
}

TEST(SnapshotTest, CopiesOnlyDirtyBitsAndClears)
{
    CellGrid g(4, 3, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'A'));
    g.putCell(2, 1, makeCell(u'Z'));
    std::vector<uint8_t> rows(gridByteSize(4, 3), 0);
    std::vector<uint8_t> dirty(dirtyBitmapByteSize(3), 0);
    ASSERT_TRUE(copyDirtyRows(g, rows.data(), rows.size(), dirty.data(), dirty.size()));
    EXPECT_EQ(dirty[0] & 0x01, 0x01); // row 0
    EXPECT_EQ(dirty[0] & 0x02, 0);    // row 1
    EXPECT_EQ(dirty[0] & 0x04, 0x04); // row 2
    EXPECT_FALSE(g.isDirty(0));
    EXPECT_FALSE(g.isDirty(2));
    Cell first{};
    std::memcpy(&first, rows.data(), sizeof(Cell));
    EXPECT_EQ(first.codepoint, u'A');
}

TEST(SnapshotTest, CopyViewportOffsetZeroIsLiveGrid)
{
    CellGrid g(2, 2, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'A'));
    g.putCell(1, 0, makeCell(u'B'));
    ScrollbackBuffer sb(2, 8);
    std::vector<uint8_t> rows(gridByteSize(2, 2), 0);
    copyViewport(g, sb, 0, rows.data(), rows.size());
    Cell a{};
    std::memcpy(&a, rows.data(), sizeof(Cell));
    EXPECT_EQ(a.codepoint, u'A');
    Cell b{};
    std::memcpy(&b, rows.data() + 2 * sizeof(Cell), sizeof(Cell));
    EXPECT_EQ(b.codepoint, u'B');
}

TEST(SnapshotTest, CopyViewportPullsScrollbackWhenOffsetPositive)
{
    CellGrid g(2, 2, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'C'));
    g.putCell(1, 0, makeCell(u'D'));
    ScrollbackBuffer sb(2, 8);
    Cell line[2] = {makeCell(u'A'), makeCell(0)};
    sb.pushLine(line, 2);
    line[0] = makeCell(u'B');
    sb.pushLine(line, 2);

    std::vector<uint8_t> rows(gridByteSize(2, 2), 0);
    copyViewport(g, sb, 1, rows.data(), rows.size());
    Cell top{};
    std::memcpy(&top, rows.data(), sizeof(Cell));
    EXPECT_EQ(top.codepoint, u'B'); // 上移 1：回滚最新一行 + 屏首行
    Cell mid{};
    std::memcpy(&mid, rows.data() + 2 * sizeof(Cell), sizeof(Cell));
    EXPECT_EQ(mid.codepoint, u'C');
}

TEST(SnapshotTest, GetTextJoinsSoftWrappedLines)
{
    CellGrid g(4, 2, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'A'));
    g.putCell(0, 1, makeCell(u'B'));
    g.putCell(0, 3, makeCell(u'-', kAttrSoftWrap));
    g.putCell(1, 0, makeCell(u'C'));
    g.putCell(1, 1, makeCell(u'D'));
    ScrollbackBuffer sb(4, 4);
    EXPECT_EQ(getText(g, sb, 0, 0, 1, 3, 0), "AB-CD");
}

TEST(SnapshotTest, GetTextInsertsNewlineWithoutSoftWrap)
{
    CellGrid g(4, 2, kDefaultFgMarker, kDefaultBgMarker);
    g.putCell(0, 0, makeCell(u'A'));
    g.putCell(1, 0, makeCell(u'B'));
    ScrollbackBuffer sb(4, 4);
    EXPECT_EQ(getText(g, sb, 0, 0, 1, 3, 0), "A\nB");
}
