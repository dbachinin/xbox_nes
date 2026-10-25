add_executable(unit_tests unit/unit_tests.cpp)
target_link_libraries(unit_tests PRIVATE nes_core)

foreach(t
    cart_valid_nrom cart_chr_ram_trainer cart_errors cart_nes2_diskdude
    cpu_load_store_flags cpu_adc_sbc_no_decimal cpu_cycle_counts cpu_jmp_indirect_bug
    cpu_interrupts cpu_unofficial_jam
    mapper_nrom mapper_uxrom_cnrom mapper_mmc1 mapper_mmc3_irq
    machine_renders_test_rom machine_input_moves_sprite machine_nmi_per_frame machine_frame_cycles machine_random_prg_no_crash
    apu_length_samples)
  add_test(NAME unit.${t} COMMAND unit_tests ${t})
endforeach()

add_executable(c_api_tests unit/c_api_tests.cpp)
target_link_libraries(c_api_tests PRIVATE nescore)
target_include_directories(c_api_tests PRIVATE ${CMAKE_SOURCE_DIR}/core/include)
add_test(NAME unit.c_api_round_trip COMMAND c_api_tests c_api_round_trip)

# Downloaded diagnostic ROMs (scripts/fetch-test-roms.sh). Missing fixtures => SKIPPED, not passed.
set(NES_FIXTURES "${CMAKE_SOURCE_DIR}/tests/fixtures/roms" CACHE PATH "Directory with downloaded test ROMs")

add_executable(nestest_log integration/nestest_log.cpp)
target_link_libraries(nestest_log PRIVATE nes_core)
add_test(NAME rom.nestest COMMAND nestest_log ${NES_FIXTURES}/nestest.nes ${NES_FIXTURES}/nestest.log)
set_tests_properties(rom.nestest PROPERTIES SKIP_RETURN_CODE 77)

# blargg tests that report through $6000. Only ones that currently pass are listed;
# see docs/STATUS.md for the full compatibility table.
foreach(r official_only all_instrs apu_test)
  add_test(NAME rom.blargg.${r}
    COMMAND ${CMAKE_COMMAND} -DCLI=$<TARGET_FILE:nes_cli> -DROM=${NES_FIXTURES}/${r}.nes
            -P ${CMAKE_CURRENT_SOURCE_DIR}/integration/run_blargg.cmake)
  set_tests_properties(rom.blargg.${r} PROPERTIES SKIP_RETURN_CODE 77)
endforeach()

# Licensed homebrew smoke test (Cybercoaster, CC-BY-4.0, MMC3): boots and draws non-blank frames.
add_test(NAME rom.homebrew.cybercoaster
  COMMAND ${CMAKE_COMMAND} -DCLI=$<TARGET_FILE:nes_cli> -DROM=${NES_FIXTURES}/cybercoaster.nes
          -P ${CMAKE_CURRENT_SOURCE_DIR}/integration/run_smoke.cmake)
set_tests_properties(rom.homebrew.cybercoaster PROPERTIES SKIP_RETURN_CODE 77)

# Managed shell logic + P/Invoke into the same libnescore.so (needs the .NET SDK; skipped otherwise).
find_program(DOTNET_EXECUTABLE dotnet)
if(DOTNET_EXECUTABLE)
  add_test(NAME managed.logic
    COMMAND ${CMAKE_COMMAND} -E env NESCORE_PATH=$<TARGET_FILE:nescore> DOTNET_CLI_TELEMETRY_OPTOUT=1
            ${DOTNET_EXECUTABLE} run -c Release --project ${CMAKE_SOURCE_DIR}/xbox/NesXbox.Logic.Tests)
  set_tests_properties(managed.logic PROPERTIES TIMEOUT 600)
endif()
