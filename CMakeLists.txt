cmake_minimum_required(VERSION 3.20)
project(nes_xbox LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

option(NES_SANITIZE "Build with ASan + UBSan" OFF)
if(NES_SANITIZE)
  add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer -fno-sanitize-recover=all)
  add_link_options(-fsanitize=address,undefined)
endif()
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  add_compile_options(-Wall -Wextra -Wpedantic)
endif()

set(NES_CORE_SOURCES
  core/src/apu.cpp
  core/src/cartridge.cpp
  core/src/cpu6502.cpp
  core/src/emulator.cpp
  core/src/mappers.cpp
  core/src/ppu.cpp)

add_library(nes_core STATIC ${NES_CORE_SOURCES})
target_include_directories(nes_core PUBLIC core/include)

# Same C ABI the Xbox shell P/Invokes; built here so the managed tests can load it on Linux.
add_library(nescore SHARED core/src/nes_c_api.cpp)
target_link_libraries(nescore PRIVATE nes_core)
target_compile_definitions(nescore PRIVATE NES_CORE_BUILD)
set_target_properties(nescore PROPERTIES CXX_VISIBILITY_PRESET hidden)

add_executable(nes_cli tools/nes_cli/main.cpp)
target_link_libraries(nes_cli PRIVATE nes_core)

include(CTest)
if(BUILD_TESTING)
  add_subdirectory(tests)
endif()
