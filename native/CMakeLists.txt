cmake_minimum_required(VERSION 3.25)
project(PulseGenEngine VERSION 0.1.0 LANGUAGES CXX)

option(PULSEGEN_BUILD_TESTS "Build native engine tests" ON)

add_library(PulseGen.Engine SHARED
    src/pulsegen.cpp
    src/wav_writer.cpp
)

target_compile_features(PulseGen.Engine PUBLIC cxx_std_20)
target_include_directories(PulseGen.Engine
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)
target_compile_definitions(PulseGen.Engine PRIVATE PULSEGEN_ENGINE_BUILD=1)
set_target_properties(PulseGen.Engine PROPERTIES
    OUTPUT_NAME "PulseGen.Engine"
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
)

if(MSVC)
    target_compile_options(PulseGen.Engine PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
else()
    target_compile_options(PulseGen.Engine PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

include(CTest)
if(PULSEGEN_BUILD_TESTS)
    add_executable(PulseGen.Engine.Tests tests/engine_tests.cpp)
    target_compile_features(PulseGen.Engine.Tests PRIVATE cxx_std_20)
    target_link_libraries(PulseGen.Engine.Tests PRIVATE PulseGen.Engine)
    if(MSVC)
        target_compile_options(PulseGen.Engine.Tests PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
    else()
        target_compile_options(PulseGen.Engine.Tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME PulseGen.Engine.Tests COMMAND PulseGen.Engine.Tests)
endif()

install(TARGETS PulseGen.Engine
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(DIRECTORY include/ DESTINATION include)

