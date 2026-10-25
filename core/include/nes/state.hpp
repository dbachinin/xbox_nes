#pragma once

#include <array>
#include <cstdint>
#include <cstring>
#include <type_traits>
#include <vector>

namespace nes {

// Two-way save-state archive: each component lists its fields once in state(StateIO&),
// and the same code saves (writing) or restores (loading). Reads are bounds-checked;
// a short or oversized buffer flips ok() to false instead of reading out of range.
class StateIO {
public:
    // Saving: appends to `out`.
    explicit StateIO(std::vector<std::uint8_t>& out) : out_(&out) {}
    // Loading: reads from [data, data + size).
    StateIO(const std::uint8_t* data, std::size_t size) : in_(data), size_(size) {}

    bool loading() const { return in_ != nullptr; }
    bool ok() const { return ok_; }
    bool finished() const { return !loading() || pos_ == size_; }

    void bytes(void* p, std::size_t n) {
        if (!loading()) {
            auto* b = static_cast<const std::uint8_t*>(p);
            out_->insert(out_->end(), b, b + n);
        } else if (ok_ && n <= size_ - pos_) {
            std::memcpy(p, in_ + pos_, n);
            pos_ += n;
        } else {
            ok_ = false;
        }
    }

    template <typename T>
    void operator()(T& v) {
        static_assert(std::is_trivially_copyable_v<T>, "state field must be trivially copyable");
        bytes(&v, sizeof v);
    }

    // Variable-size buffers (PRG/CHR RAM) keep their size; a mismatching size fails the load.
    void operator()(std::vector<std::uint8_t>& v) {
        std::uint32_t n = static_cast<std::uint32_t>(v.size());
        (*this)(n);
        if (loading() && n != v.size()) { ok_ = false; return; }
        bytes(v.data(), v.size());
    }

private:
    std::vector<std::uint8_t>* out_ = nullptr;
    const std::uint8_t* in_ = nullptr;
    std::size_t size_ = 0, pos_ = 0;
    bool ok_ = true;
};

} // namespace nes
