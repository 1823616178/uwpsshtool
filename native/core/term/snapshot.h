/**
 * T03：网格快照纯函数（01-DESIGN §4.3 / §6.2）。
 * 不碰 WinRT / libvterm；只读 CellGrid + ScrollbackBuffer。
 */
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

#include "grid.h"
#include "scrollback.h"

namespace sshclient {
namespace term {

inline size_t gridByteSize(int cols, int rows)
{
    return static_cast<size_t>(cols) * static_cast<size_t>(rows) * sizeof(Cell);
}

inline size_t dirtyBitmapByteSize(int rows)
{
    return static_cast<size_t>((rows + 7) / 8);
}

// 有脏行则把整屏拷到 rowsOut、脏位图按「每行 1 bit、字节内低位先行」写入 dirtyOut，
// 然后清零网格脏位，返回 true。无脏行或缓冲区不够大返回 false（不写、不清）。
bool copyDirtyRows(CellGrid &grid, uint8_t *rowsOut, size_t rowsCap,
                   uint8_t *dirtyOut, size_t dirtyCap);

// 从底部向上偏移 offset 行的视口（offset=0 即当前屏）。rowsOut 须能装下 rows*cols 格。
// 缓冲区不够则不写。
void copyViewport(const CellGrid &grid, const ScrollbackBuffer &scrollback, int offset,
                  uint8_t *rowsOut, size_t rowsCap);

// 视口坐标系下的矩形选区（含端点），软换行不插入换行。offset 同 copyViewport。
std::string getText(const CellGrid &grid, const ScrollbackBuffer &scrollback,
                    int startRow, int startCol, int endRow, int endCol, int offset);

} // namespace term
} // namespace sshclient
