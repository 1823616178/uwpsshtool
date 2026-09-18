#include "snapshot.h"

#include <algorithm>
#include <cstring>
#include <vector>

namespace sshclient {
namespace term {
namespace {

void packDirtyBytes(const CellGrid &grid, uint8_t *dirtyOut, size_t dirtyCap)
{
    const int rows = grid.rows();
    const size_t n = dirtyBitmapByteSize(rows);
    std::memset(dirtyOut, 0, (std::min)(n, dirtyCap));
    for (int r = 0; r < rows; ++r) {
        if (grid.isDirty(r))
            dirtyOut[static_cast<size_t>(r) / 8] |= static_cast<uint8_t>(1u << (r % 8));
    }
}

const Cell *lineAt(const CellGrid &grid, const ScrollbackBuffer &sb, int offset, int viewRow)
{
    const int rows = grid.rows();
    const int cols = grid.cols();
    if (viewRow < 0 || viewRow >= rows || cols <= 0)
        return nullptr;
    const int off = (std::max)(offset, 0);
    const size_t sbSize = sb.size();
    const uint64_t total = static_cast<uint64_t>(sbSize) + static_cast<uint64_t>(rows);
    const uint64_t bottom = total - 1;
    if (static_cast<uint64_t>(off) > bottom)
        return nullptr;
    const uint64_t windowEnd = bottom - static_cast<uint64_t>(off);
    const uint64_t windowStart = (windowEnd + 1 >= static_cast<uint64_t>(rows))
        ? (windowEnd + 1 - static_cast<uint64_t>(rows))
        : 0;
    const uint64_t absIndex = windowStart + static_cast<uint64_t>(viewRow);
    if (absIndex >= total)
        return nullptr;
    if (absIndex < sbSize)
        return sb.getLine(sb.oldestIndex() + absIndex);
    const int gridRow = static_cast<int>(absIndex - sbSize);
    return grid.cellAt(gridRow, 0);
}

void appendCellText(std::string &out, const Cell &cell)
{
    if (cell.codepoint == 0 || cell.codepoint == kWideContinuation)
        return;
    if (cell.codepoint < 128)
        out.push_back(static_cast<char>(cell.codepoint));
    else {
        // UTF-8 encode BMP+ (T03 选择复制；组合符未存)
        uint32_t cp = cell.codepoint;
        if (cp <= 0x7F)
            out.push_back(static_cast<char>(cp));
        else if (cp <= 0x7FF) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp <= 0xFFFF) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }
}

} // namespace

bool copyDirtyRows(CellGrid &grid, uint8_t *rowsOut, size_t rowsCap,
                   uint8_t *dirtyOut, size_t dirtyCap)
{
    if (rowsOut == nullptr || dirtyOut == nullptr)
        return false;
    const int cols = grid.cols();
    const int rows = grid.rows();
    const size_t needRows = gridByteSize(cols, rows);
    const size_t needDirty = dirtyBitmapByteSize(rows);
    if (rowsCap < needRows || dirtyCap < needDirty)
        return false;

    bool any = false;
    for (int r = 0; r < rows; ++r) {
        if (grid.isDirty(r)) {
            any = true;
            break;
        }
    }
    if (!any)
        return false;

    std::memcpy(rowsOut, grid.data(), needRows);
    packDirtyBytes(grid, dirtyOut, dirtyCap);
    grid.clearDirty();
    return true;
}

void copyViewport(const CellGrid &grid, const ScrollbackBuffer &scrollback, int offset,
                  uint8_t *rowsOut, size_t rowsCap)
{
    if (rowsOut == nullptr)
        return;
    const int cols = grid.cols();
    const int rows = grid.rows();
    const size_t need = gridByteSize(cols, rows);
    if (rowsCap < need)
        return;
    std::memset(rowsOut, 0, need);
    for (int r = 0; r < rows; ++r) {
        const Cell *line = lineAt(grid, scrollback, offset, r);
        if (line == nullptr)
            continue;
        std::memcpy(rowsOut + static_cast<size_t>(r) * static_cast<size_t>(cols) * sizeof(Cell),
                    line, static_cast<size_t>(cols) * sizeof(Cell));
    }
}

std::string getText(const CellGrid &grid, const ScrollbackBuffer &scrollback,
                    int startRow, int startCol, int endRow, int endCol, int offset)
{
    const int cols = grid.cols();
    const int rows = grid.rows();
    if (cols <= 0 || rows <= 0)
        return {};
    startRow = (std::max)(0, (std::min)(startRow, rows - 1));
    endRow = (std::max)(0, (std::min)(endRow, rows - 1));
    startCol = (std::max)(0, (std::min)(startCol, cols - 1));
    endCol = (std::max)(0, (std::min)(endCol, cols - 1));
    if (endRow < startRow || (endRow == startRow && endCol < startCol)) {
        std::swap(startRow, endRow);
        std::swap(startCol, endCol);
    }

    std::string out;
    for (int r = startRow; r <= endRow; ++r) {
        const Cell *line = lineAt(grid, scrollback, offset, r);
        if (line == nullptr)
            continue;
        const int c0 = (r == startRow) ? startCol : 0;
        const int c1 = (r == endRow) ? endCol : (cols - 1);
        for (int c = c0; c <= c1; ++c)
            appendCellText(out, line[c]);
        const bool soft = (line[cols - 1].attrs & kAttrSoftWrap) != 0;
        if (r < endRow && !soft)
            out.push_back('\n');
    }
    return out;
}

} // namespace term
} // namespace sshclient
