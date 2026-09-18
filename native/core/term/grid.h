/**
 * CellGrid —— 终端单元格网格（任务 T01，01-DESIGN.md §7.1）。
 *
 * 布局硬性约定：每格 16 字节连续内存，小端：
 *   偏移 0  u32  Unicode 码点（0 = 空；kWideContinuation = 宽字符续格标记）
 *   偏移 4  u32  前景色 ARGB（已解析调色板/真彩；默认色用 kDefaultFgMarker）
 *   偏移 8  u32  背景色 ARGB（默认色用 kDefaultBgMarker）
 *   偏移 12 u16  属性位（kAttr* 系列）
 *   偏移 14 u16  保留（链接 id / 未来扩展）
 * static_assert 钉死尺寸与偏移，改动会编译期报错。
 *
 * 本文件是纯逻辑实现：只依赖 C++ 标准库，禁止 include <vterm.h>。
 * 同时被 UWP NativeCore 与宿主机 native/tests 编译。
 *
 * 不变式：单元格内容写入必须经由 putCell / fillCells / copyCells，
 * 它们负责「脏行位图置位 + revision +1」；cellAt 返回的可写指针只供
 * 读路径与确知自己在做什么的写入者（写后须自行 touchCell），
 * 否则脏行位图与 revision 会与实际内容脱节。
 * 两条例外路径：bumpRevision（光标移动等非内容性视觉变化，只抬 revision）
 * 与 raiseRevisionFloor（整体重建后保 revision 单调不减），均不写单元格。
 *
 * 存储所有权（T03 快照生命周期保护）：
 *   单元格存储为 shared_ptr<std::vector<Cell>>。resize 换入新 vector，
 *   旧 vector 由 cellsStorage() 流出的 shared_ptr 副本保活。
 *   拷贝语义保持值语义（深拷贝存储），不共享。
 */
#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <vector>

namespace sshclient {
namespace term {

// 宽字符续格码点标记（01-DESIGN §7.1）
inline constexpr uint32_t kWideContinuation = 0xFFFFFFFFu;

// 默认色标记：单元格存标记而非写死黑白，外观切换无需重放（§7.1）。
inline constexpr uint32_t kDefaultFgMarker = 0x00000001u;
inline constexpr uint32_t kDefaultBgMarker = 0x00000002u;

// 属性位（偏移 12 的 u16）。dim 位预留：libvterm 0.3.3 屏幕层没有 dim。
inline constexpr uint16_t kAttrBold      = 1u << 0;
inline constexpr uint16_t kAttrItalic    = 1u << 1;
inline constexpr uint16_t kAttrUnderline = 1u << 2;
inline constexpr uint16_t kAttrBlink     = 1u << 3;
inline constexpr uint16_t kAttrReverse   = 1u << 4;
inline constexpr uint16_t kAttrStrike    = 1u << 5;
inline constexpr uint16_t kAttrDim       = 1u << 6;
inline constexpr uint16_t kAttrWide      = 1u << 7; // 宽字符首格
inline constexpr uint16_t kAttrInvisible = 1u << 8; // SGR 8 conceal
inline constexpr uint16_t kAttrSoftWrap  = 1u << 9; // 本行软换行续接（写在行末格）

struct Cell {
    uint32_t codepoint; // 偏移 0：Unicode 码点；0 = 空；kWideContinuation = 宽字符续格
    uint32_t fgArgb;    // 偏移 4：前景色 ARGB
    uint32_t bgArgb;    // 偏移 8：背景色 ARGB
    uint16_t attrs;     // 偏移 12：属性位（kAttr*）
    uint16_t reserved;  // 偏移 14：保留（链接 id / 未来扩展）
};

static_assert(sizeof(Cell) == 16, "Cell 必须恰好 16 字节（01-DESIGN §7.1）");
static_assert(offsetof(Cell, codepoint) == 0, "Cell 布局偏离 01-DESIGN §7.1");
static_assert(offsetof(Cell, fgArgb) == 4, "Cell 布局偏离 01-DESIGN §7.1");
static_assert(offsetof(Cell, bgArgb) == 8, "Cell 布局偏离 01-DESIGN §7.1");
static_assert(offsetof(Cell, attrs) == 12, "Cell 布局偏离 01-DESIGN §7.1");
static_assert(offsetof(Cell, reserved) == 14, "Cell 布局偏离 01-DESIGN §7.1");

class CellGrid {
public:
    // 默认前/背景色由上层外观系统注入（native 侧先为可配构造参数）
    CellGrid(int cols, int rows, uint32_t defaultFgArgb, uint32_t defaultBgArgb);

    // 值语义深拷贝（存储 shared_ptr 不共享，见头注「存储所有权」）
    CellGrid(const CellGrid &other);
    CellGrid &operator=(const CellGrid &other);
    CellGrid(CellGrid &&) = default;
    CellGrid &operator=(CellGrid &&) = default;

    int cols() const { return cols_; }
    int rows() const { return rows_; }
    uint32_t defaultFgArgb() const { return defaultFgArgb_; }
    uint32_t defaultBgArgb() const { return defaultBgArgb_; }

    // 空白格：码点 0 + 默认色 + 无属性（erase/清屏后的标准内容）
    Cell blankCell() const;

    // 越界是调用方 bug：debug 下 assert，release 下未定义（与 vector::operator[] 同级约定）
    const Cell *cellAt(int row, int col) const { return &(*cells_)[index(row, col)]; }
    Cell *cellAt(int row, int col) { return &(*cells_)[index(row, col)]; } // 写后须 touchCell

    // 连续内存起点与字节数（T3 零拷贝快照直接暴露这段内存）
    const uint8_t *data() const { return reinterpret_cast<const uint8_t *>(cells_->data()); }
    size_t byteSize() const { return cells_->size() * sizeof(Cell); }

    // 当前单元格存储的共享所有权句柄（T3 零拷贝快照用）：与网格当前存储指向
    // 同一 vector；此后 resize/setDefaultColors 换入新存储，本返回值仍保活旧 vector
    //（配合 napi external arraybuffer 的 finalize 延迟回收，见头注）
    std::shared_ptr<const std::vector<Cell>> cellsStorage() const { return cells_; }

    // 标准写路径：写一格 + 脏行置位 + revision +1
    void putCell(int row, int col, const Cell &cell);
    // 经 cellAt 可写指针直接写完后，手工维护脏行/revision
    void touchCell(int row, int col);

    // 用 cell 填充半开矩形 [row0,row1) × [col0,col1)（ED/EL 用；越界部分裁剪）
    void fillCells(int row0, int col0, int row1, int col1, const Cell &cell);

    // 拷贝矩形区域（矩形搬移原语；VtermBridge 滚动不镜像拷贝、走 damage 重读，
    // 见 vterm_screen.cpp onMoveRect），源/目标允许重叠（内部走临时快照）；越界部分裁剪
    void copyCells(int destRow, int destCol, int srcRow, int srcCol, int rowCount, int colCount);

    // 改尺寸：按行拷贝行列交集保留内容，新增区域填空白格；全部行标脏
    void resize(int newCols, int newRows);

    // 脏行位图（每行 1 bit，vector<uint64_t> 按 64 行一个 word）
    void setDirty(int row);
    void clearDirty(); // 全部清零（渲染帧消费完脏行后调用）
    bool isDirty(int row) const;
    const std::vector<uint64_t> &dirtyBitmap() const { return dirty_; }

    // 单调递增修订号：每次单元格内容写入 +1（ArkTS 每帧比对，无变化跳过渲染）
    uint64_t revision() const { return revision_; }

    // 非内容性视觉变化（光标移动）抬 revision：帧循环以 revision 判「是否重绘」，
    // 纯光标移动不写单元格、不抬会丢光标帧（T1 审查跟进项，T2 修）。
    // 只抬计数，不碰脏行位图——新旧光标行的标脏由调用方负责。
    void bumpRevision() { ++revision_; }

    // 把 revision 抬到至少 floor（floor 更大时生效，否则不动）。
    // 用途：setDefaultColors 这类「整体重建网格」后 revision 从 0 重计数会对外回退，
    // 调用方重建后抬回旧值，保证 revision 对外单调不减（帧循环不比小）。
    void raiseRevisionFloor(uint64_t floor)
    {
        if (revision_ < floor)
            revision_ = floor;
    }

private:
    size_t index(int row, int col) const;

    int cols_;
    int rows_;
    uint32_t defaultFgArgb_;
    uint32_t defaultBgArgb_;
    // cols*rows 连续内存，行优先；shared_ptr 持有的原因见头注「存储所有权」（T3）。
    // 永不为空（构造即分配）；resize 换入新 vector，旧 vector 由流出的副本保活
    std::shared_ptr<std::vector<Cell>> cells_;
    std::vector<uint64_t> dirty_;   // (rows+63)/64 个 word
    uint64_t revision_ = 0;
};

} // namespace term
} // namespace sshclient
