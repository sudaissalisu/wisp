cmake_minimum_required(VERSION 3.20)
project(wisp_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

option(WISP_BUILD_TESTS "Build the native test harness" ON)

if(MSVC)
    add_compile_options(/utf-8 /permissive- /EHsc /W4)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

set(WISP_SOURCES
    src/util.cpp
    src/net.cpp
    src/identity.cpp
    src/protocol.cpp
    src/file_io.cpp
    src/discovery.cpp
    src/engine.cpp
    src/engine_receive.cpp
    src/engine_send.cpp
)

add_library(wisp_native STATIC ${WISP_SOURCES})
target_include_directories(wisp_native PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
)


if(ANDROID)
    set(WISP_ANDROID_OPENSSL_ROOT "C:/Users/Dreadshades/wisp-tools/android_openssl" CACHE PATH
        "Path to the prebuilt Android OpenSSL tree")
    set(WISP_ANDROID_OPENSSL_DIR "${WISP_ANDROID_OPENSSL_ROOT}/ssl_3/${ANDROID_ABI}")
    if(NOT EXISTS "${WISP_ANDROID_OPENSSL_DIR}/libssl.a")
        message(FATAL_ERROR "Android OpenSSL libraries not found at ${WISP_ANDROID_OPENSSL_DIR}")
    endif()
    add_library(OpenSSL::Crypto STATIC IMPORTED)
    set_target_properties(OpenSSL::Crypto PROPERTIES
        IMPORTED_LOCATION "${WISP_ANDROID_OPENSSL_DIR}/libcrypto.a"
        INTERFACE_INCLUDE_DIRECTORIES "${WISP_ANDROID_OPENSSL_ROOT}/ssl_3/include"
    )
    add_library(OpenSSL::SSL STATIC IMPORTED)
    set_target_properties(OpenSSL::SSL PROPERTIES
        IMPORTED_LOCATION "${WISP_ANDROID_OPENSSL_DIR}/libssl.a"
        INTERFACE_INCLUDE_DIRECTORIES "${WISP_ANDROID_OPENSSL_ROOT}/ssl_3/include"
        INTERFACE_LINK_LIBRARIES OpenSSL::Crypto
    )
    find_package(Threads REQUIRED)
    target_link_libraries(wisp_native PUBLIC OpenSSL::SSL Threads::Threads log dl)
    target_compile_options(wisp_native PRIVATE -fPIC)
    add_library(wisp_core SHARED src/capi.cpp)
    target_compile_definitions(wisp_core PRIVATE WISP_BUILDING)
    target_link_libraries(wisp_core PRIVATE wisp_native)
    target_link_options(wisp_core PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
    set_target_properties(wisp_core PROPERTIES OUTPUT_NAME "wisp_core")
    return()
endif()

if(WIN32)
    set(OPENSSL_ROOT_DIR "C:/Program Files/OpenSSL-Win64" CACHE PATH "OpenSSL root")
    set(OPENSSL_LIB_DIR "${OPENSSL_ROOT_DIR}/lib/VC/x64/MD")
    add_library(OpenSSL::Crypto UNKNOWN IMPORTED)
    set_target_properties(OpenSSL::Crypto PROPERTIES
        IMPORTED_LOCATION "${OPENSSL_LIB_DIR}/libcrypto.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${OPENSSL_ROOT_DIR}/include"
    )
    add_library(OpenSSL::SSL UNKNOWN IMPORTED)
    set_target_properties(OpenSSL::SSL PROPERTIES
        IMPORTED_LOCATION "${OPENSSL_LIB_DIR}/libssl.lib"
        INTERFACE_INCLUDE_DIRECTORIES "${OPENSSL_ROOT_DIR}/include"
        INTERFACE_LINK_LIBRARIES OpenSSL::Crypto
    )
    set(WISP_PLATFORM_LIBS ws2_32 crypt32 iphlpapi)
    set(OPENSSL_DLLS
        "${OPENSSL_ROOT_DIR}/bin/libssl-3-x64.dll"
        "${OPENSSL_ROOT_DIR}/bin/libcrypto-3-x64.dll"
    )
else()
    find_package(OpenSSL REQUIRED)
    set(WISP_PLATFORM_LIBS "")
    set(OPENSSL_DLLS "")
endif()

find_package(Threads REQUIRED)
target_link_libraries(wisp_native PUBLIC OpenSSL::SSL Threads::Threads ${WISP_PLATFORM_LIBS})

add_library(wisp_core SHARED src/capi.cpp)
target_compile_definitions(wisp_core PRIVATE WISP_BUILDING)
target_link_libraries(wisp_core PRIVATE wisp_native)
set_target_properties(wisp_core PROPERTIES OUTPUT_NAME "wisp_core")

add_executable(wisp_cli cli/main.cpp)
target_include_directories(wisp_cli PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(wisp_cli PRIVATE wisp_core)
set_target_properties(wisp_cli PROPERTIES OUTPUT_NAME "wisp_cli")

function(wisp_copy_runtime target)
    if(OPENSSL_DLLS)
        add_custom_command(TARGET ${target} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different ${OPENSSL_DLLS}
                    "$<TARGET_FILE_DIR:${target}>"
        )
    endif()
endfunction()

wisp_copy_runtime(wisp_cli)

if(WISP_BUILD_TESTS)
    enable_testing()
    add_executable(wisp_native_tests tests/main.cpp)
    target_link_libraries(wisp_native_tests PRIVATE wisp_native)
    wisp_copy_runtime(wisp_native_tests)
    add_test(NAME wisp_native_tests COMMAND wisp_native_tests)
endif()
