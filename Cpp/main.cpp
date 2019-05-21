#ifdef WIN32
#define Windows
#else
#define Unix
#endif

#include <fcntl.h>
#include <immintrin.h>
#ifdef Unix
#include <sys/mman.h>
#endif
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#include <cstdio>
#include <cstdint>
#include <cstring>

#include <functional>
#include <iostream>
#include <vector>
#include <chrono>

#ifdef Unix
#define O_BINARY 0
#endif


using namespace std;

auto readRLEByte(char* fileName) {
    auto fIn = open(fileName, O_RDONLY | O_BINARY, 0644);

    static constexpr size_t BUFFER_SIZE = 1 << 16;
    uint8_t buffer[BUFFER_SIZE];
    size_t bufferPos = 0;
    size_t bufferLen = 0;

    auto readByte = [&](uint8_t& result) {
        if (bufferLen == bufferPos) {
            bufferLen = read(fIn, buffer, BUFFER_SIZE);
            bufferPos = 0;
        }
        if (bufferLen == bufferPos) {
            return false;
        }
        result = buffer[bufferPos++];
        return true;
    };

    int64_t sum = 0;
    uint8_t b;
    while (readByte(b)) {
        int n = 0;
        while (b < 128) {
            n = (n << 7) + b;
            if (!readByte(b)) {
                sum += n;
                break;
            }
        }
        n = (n << 7) + b - 128;
        sum += n;
    }
    close(fIn);
    return sum;
}

auto readRLEBuffer(char* fileName) {
    auto fIn = open(fileName, O_RDONLY | O_BINARY, 0644);

    static constexpr size_t BUFFER_SIZE = 1 << 16;
    uint8_t buffer[BUFFER_SIZE];
    uint8_t const* pBuffer = nullptr;
    size_t bufferPos = 0;
    size_t bufferLen = 0;

    int64_t sum = 0;
    uint8_t b;
    int n = 0;
    while (bufferLen = read(fIn, buffer, BUFFER_SIZE)) {
        pBuffer = buffer;
        const uint8_t* const pBufferEnd = buffer + bufferLen;
        while (pBuffer != pBufferEnd) {
            if (*pBuffer < 128) {
                n = (n << 7) + *pBuffer;
            } else {
                n = (n << 7) + *pBuffer - 128;
                sum += n;
                n = 0;
            }
            ++pBuffer;
        }
    }
    close(fIn);
    return sum;
}

#ifdef Unix
auto readRLEMmap(char* fileName) {
    auto fIn = open(fileName, O_RDONLY | O_BINARY, 0644);

    struct stat s;
    fstat(fIn, &s);
    auto buffer = reinterpret_cast<const uint8_t* const>(mmap(nullptr, s.st_size, PROT_WRITE, MAP_PRIVATE, fIn, 0));

    int64_t sum = 0;
    uint8_t b;
    int n = 0;
    const uint8_t* const pBufferEnd = buffer + s.st_size;
    auto pBuffer = buffer;
    while (pBuffer != pBufferEnd) {
        if (*pBuffer < 128) {
            n = (n << 7) + *pBuffer;
        } else {
            n = (n << 7) + *pBuffer - 128;
            sum += n;
            n = 0;
        }
        ++pBuffer;
    }
    close(fIn);
    return sum;
}
#endif

auto readRLEMmapSIMD(char* fileName) {
    auto fIn = open(fileName, O_RDONLY | O_BINARY, 0644);

    struct stat s;
    fstat(fIn, &s);
    auto buffer = reinterpret_cast<const uint8_t* const>(mmap(nullptr, s.st_size, PROT_WRITE, MAP_PRIVATE, fIn, 0));
    madvise(const_cast<void*>(reinterpret_cast<const void*>(buffer)), s.st_size, MADV_SEQUENTIAL | MADV_WILLNEED);

    const uint8_t* const pBufferEnd = buffer + s.st_size;
    static constexpr size_t N = 32;
    const uint8_t* const pBufferEnd32 = buffer + (s.st_size & ~31) - 32;
    const uint8_t* pBuffer = buffer;
    const __m256i mask0 = _mm256_set1_epi8(0x7F);
    const __m256i mask1 = _mm256_set1_epi8(0x80);
    const __m256i zero = _mm256_setzero_si256();
    __m256i s0 = _mm256_setzero_si256();
    __m256i s1 = _mm256_setzero_si256();
    __m256i s2 = _mm256_setzero_si256();
    __m256i s3 = _mm256_setzero_si256();

    for (; pBuffer != pBufferEnd32; pBuffer += N) {
        __m256i a = _mm256_lddqu_si256(reinterpret_cast<const __m256i*>(pBuffer));  // _mm256_load_si256
        __m256i as = _mm256_lddqu_si256(reinterpret_cast<const __m256i*>(pBuffer + 1));
        __m256i ass = _mm256_lddqu_si256(reinterpret_cast<const __m256i*>(pBuffer + 2));
        __m256i b = _mm256_and_si256(a, mask1);
        __m256i bs = _mm256_and_si256(as, mask1);
        __m256i bss = _mm256_and_si256(ass, mask1);
        a = _mm256_and_si256(a, mask0);
        __m256i b0 = _mm256_cmpeq_epi8(b, mask1);
        __m256i a0 = _mm256_and_si256(a, b0);
        __m256i b1 = _mm256_cmpgt_epi8(b, bs);
        __m256i a1 = _mm256_and_si256(a, b1);
        __m256i bt = _mm256_or_si256(b, bs);
        __m256i b2 = _mm256_cmpgt_epi8(bt, mask1);
        __m256i a2 = _mm256_and_si256(a, b2);
        __m256i btt = _mm256_or_si256(_mm256_or_si256(b, bs), bss);
        __m256i b3 = _mm256_cmpgt_epi8(btt, mask1);
        __m256i a3 = _mm256_and_si256(a, b3);
        a0 = _mm256_sad_epu8(a0, zero);
        a1 = _mm256_sad_epu8(a1, zero);
        a2 = _mm256_sad_epu8(a2, zero);
        a3 = _mm256_sad_epu8(a3, zero);
        s0 = _mm256_add_epi64(s0, a0);
        s1 = _mm256_add_epi64(s1, a1);
        s2 = _mm256_add_epi64(s2, a2);
        s3 = _mm256_add_epi64(s3, a3);
    }

    s1 = _mm256_slli_epi64(s1, 7);
    s2 = _mm256_slli_epi64(s2, 14);
    s3 = _mm256_slli_epi64(s3, 21);
    s0 = _mm256_add_epi64(s0, s1);
    s0 = _mm256_add_epi64(s0, s2);
    s0 = _mm256_add_epi64(s0, s3);
    uint64_t n = 0;
    uint64_t sum = _mm256_extract_epi64(s0, 0) + _mm256_extract_epi64(s0, 1) + _mm256_extract_epi64(s0, 2) +
                   _mm256_extract_epi64(s0, 3);
    for (; pBuffer != pBufferEnd; ++pBuffer) {
        if (*pBuffer < 128) {
            n = (n << 7) + *pBuffer;
        } else {
            n = (n << 7) + *pBuffer - 128;
            sum += n;
            n = 0;
        }
    }

    close(fIn);
    return (int64_t) sum;
}



int main() {
    auto measure = [&](string fnName, int64_t (&fn)(char*), char* fileName) {
        auto start = chrono::steady_clock::now();
        auto sum = (fn) (fileName);
        auto end = chrono::steady_clock::now();
        auto duration = chrono::duration_cast<chrono::milliseconds>(end - start).count();
        cout << fnName << ": " << sum << " in " << duration << " ms" << endl;
    };

#ifdef Unix
    char* fileName = (char*) "/home/alex/Projects/YASB_Dan/rle.dat";
//    char* fileName = (char*) "/home/alex/Downloads/dotnet-sdk-3.0.100-preview5-011568-linux-x64.tar.gz";
#else
    char* fileName = (char*) "C:\\Downloads\\26_dump.zip";
#endif
    measure("readRLEByte", readRLEByte, fileName);
    measure("readRLEBuffer", readRLEBuffer, fileName);
#ifdef Unix
    measure("readRLEMmap", readRLEMmap, fileName);
#endif
    measure("readRLEMmapSIMD", readRLEMmapSIMD, fileName);
    return 0;
}