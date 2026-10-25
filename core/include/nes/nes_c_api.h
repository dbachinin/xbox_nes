/* Flat C ABI over nes::NesMachine, consumed by the UWP shell via P/Invoke.
 *
 * Ownership: the caller owns every buffer it passes in; the library copies
 * ROM bytes on load and copies frames/audio out on request, so no pointer
 * crosses the boundary beyond a single call. One handle must not be used
 * from two threads at once. */
#ifndef NES_C_API_H
#define NES_C_API_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(NES_CORE_BUILD)
#    define NES_API __declspec(dllexport)
#  else
#    define NES_API __declspec(dllimport)
#  endif
#else
#  define NES_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct nes_machine nes_machine;

NES_API nes_machine* nes_create(void);
NES_API void nes_destroy(nes_machine* m);

/* Returns 0 on success, otherwise a nes::RomError value (1..6). */
NES_API int nes_load_rom(nes_machine* m, const uint8_t* data, size_t size);
NES_API void nes_reset(nes_machine* m);
NES_API void nes_set_buttons(nes_machine* m, int port, uint8_t bits);
NES_API void nes_run_frame(nes_machine* m);

/* Copies the current frame as BGRA8, nearest-neighbour upscaled by `scale`
 * (1..8). Returns bytes written, or -1 if `dst_size` is too small. */
NES_API int64_t nes_copy_frame(nes_machine* m, uint8_t* dst, size_t dst_size, int scale);
NES_API uint64_t nes_frame_hash(nes_machine* m);

/* Copies up to `max_samples` mono int16 samples (48 kHz) produced by the last frame. */
NES_API size_t nes_read_audio(nes_machine* m, int16_t* dst, size_t max_samples);

/* Battery-backed PRG RAM. Size is 0 when the cartridge has no battery. */
NES_API size_t nes_battery_size(nes_machine* m);
NES_API size_t nes_battery_read(nes_machine* m, uint8_t* dst, size_t size);
NES_API size_t nes_battery_write(nes_machine* m, const uint8_t* src, size_t size);

#ifdef __cplusplus
}
#endif

#endif
