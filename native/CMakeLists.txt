cmake_minimum_required(VERSION 3.25)
project(PulseGenEngine VERSION 0.1.0 LANGUAGES C CXX)

option(PULSEGEN_BUILD_TESTS "Build native engine tests" ON)

include(FetchContent)
FetchContent_Declare(
    miniaudio
    URL https://github.com/mackron/miniaudio/archive/9634bedb5b5a2ca38c1ee7108a9358a4e233f14d.zip
    URL_HASH SHA256=d9fc392f394fba13c4d71436fea38a4af384292871d59aa7d728c11fbc4ae978
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    # PulseGen compiles the official single-header implementation itself. A
    # deliberately absent subdirectory prevents upstream examples/extras from
    # becoming unrelated build targets while retaining FetchContent verification.
    SOURCE_SUBDIR pulsegen_header_only
)
FetchContent_MakeAvailable(miniaudio)

add_library(PulseGen.Engine SHARED
    src/pulsegen.cpp
    src/wav_writer.cpp
    src/miniaudio_impl.c
)

target_compile_features(PulseGen.Engine PUBLIC cxx_std_20)
target_include_directories(PulseGen.Engine
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${miniaudio_SOURCE_DIR}
)
target_compile_definitions(PulseGen.Engine PRIVATE
    PULSEGEN_ENGINE_BUILD=1
    MA_NO_DECODING
    MA_NO_ENCODING
    MA_NO_RESOURCE_MANAGER
    MA_NO_GENERATION
)
set_target_properties(PulseGen.Engine PROPERTIES
    OUTPUT_NAME "PulseGen.Engine"
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
)

if(MSVC)
    target_compile_options(PulseGen.Engine PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/W4>
        $<$<COMPILE_LANGUAGE:CXX>:/WX>
        $<$<COMPILE_LANGUAGE:CXX>:/permissive->
        $<$<COMPILE_LANGUAGE:CXX>:/EHsc>
        $<$<COMPILE_LANGUAGE:CXX>:/utf-8>
    )
else()
    target_compile_options(PulseGen.Engine PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Werror>
    )
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

    add_executable(PulseGen.WavWriter.Tests tests/wav_writer_tests.cpp src/wav_writer.cpp)
    target_compile_features(PulseGen.WavWriter.Tests PRIVATE cxx_std_20)
    target_include_directories(PulseGen.WavWriter.Tests PRIVATE include src)
    if(MSVC)
        target_compile_options(PulseGen.WavWriter.Tests PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
    else()
        target_compile_options(PulseGen.WavWriter.Tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME PulseGen.WavWriter.Tests COMMAND PulseGen.WavWriter.Tests)
endif()

install(TARGETS PulseGen.Engine
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(DIRECTORY include/ DESTINATION include)
