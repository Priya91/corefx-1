
project(System.Security.Cryptography.Native)

set(CMAKE_INCLUDE_CURRENT_DIR ON)

# Issue 2546 - Many deprecation warnings in System.Security.Cryptography.Native on Mac OS X
add_compile_options(-Wno-deprecated-declarations)

# These are happening inside of OpenSSL-defined macros out of our control
add_compile_options(-Wno-cast-align)

add_definitions(-DPIC=1)

find_package(OpenSSL REQUIRED)

set(NATIVECRYPTO_SOURCES
    openssl.c
    pal_asn1.cpp
    pal_asn1_print.cpp
    pal_bignum.cpp
    pal_bio.cpp
    pal_ecdsa.cpp
    pal_ecc_import_export.cpp
    pal_eckey.cpp
    pal_err.cpp
    pal_evp.cpp
    pal_evp_pkey.cpp
    pal_evp_pkey_eckey.cpp
    pal_evp_pkey_rsa.cpp
    pal_evp_cipher.cpp
    pal_hmac.cpp
    pal_pkcs12.cpp
    pal_pkcs7.cpp
    pal_rsa.cpp
    pal_ssl.cpp
    pal_x509.cpp
    pal_x509_name.cpp
    pal_x509_root.cpp
    pal_x509ext.cpp
)

add_library(System.Security.Cryptography.Native
    SHARED
    ${NATIVECRYPTO_SOURCES}
    ${VERSION_FILE_PATH}
)

# Disable the "lib" prefix.
set_target_properties(System.Security.Cryptography.Native PROPERTIES PREFIX "")

target_link_libraries(System.Security.Cryptography.Native
  ${OPENSSL_CRYPTO_LIBRARY}
  ${OPENSSL_SSL_LIBRARY}
)

# On OS X every library emits the manner in which it should be referenced.
# All of our libraries are referenced via @rpath, which is similar to how Linux and Windows
# libraries are loaded. The homebrew installation of OpenSSL (libcrypto, libssl) uses the
# full path to the library installation. This means that this library is not flexible to
# users installing newer libcrypto in the working directory, or to systems which do not
# install to the same path as homebrew does.
#
# So, after compiling, rewrite the references to libcrypto to be more flexible.
if (APPLE)
    add_custom_command(TARGET System.Security.Cryptography.Native POST_BUILD
        COMMAND ${CMAKE_INSTALL_NAME_TOOL} -change /usr/local/opt/openssl/lib/libcrypto.1.0.0.dylib @rpath/libcrypto.1.0.0.dylib $<TARGET_FILE:System.Security.Cryptography.Native>
        COMMAND ${CMAKE_INSTALL_NAME_TOOL} -change /usr/local/opt/openssl/lib/libssl.1.0.0.dylib @rpath/libssl.1.0.0.dylib $<TARGET_FILE:System.Security.Cryptography.Native>
        COMMAND ${CMAKE_INSTALL_NAME_TOOL} -add_rpath @loader_path $<TARGET_FILE:System.Security.Cryptography.Native>
        )
endif()

include(configure.cmake)

install (TARGETS System.Security.Cryptography.Native DESTINATION .)
