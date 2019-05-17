#define Windows

#include <fcntl.h>
#include <unistd.h>
#include <sys/types.h>
#include <sys/stat.h>

#include <cstdio>
#include <cstdint>
#include <cstring>

#include <iostream>
#include <chrono>

#ifndef Windows
#include <sys/mman.h>
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

#ifndef Windows
void readRLEMmap(char* fileName) {
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


int main() {
    auto measure = [&](string fnName, int64_t (&fn)(char*), char* fileName) {
        auto start = chrono::steady_clock::now();
        auto sum = (fn) (fileName);
        auto end = chrono::steady_clock::now();
        auto duration = chrono::duration_cast<chrono::milliseconds>(end - start).count();
        cout << fnName << ": " << sum << " in " << duration << " ms" << endl;
    };

    char* fileName = (char*) "C:\\Downloads\\26_dump.zip";
    measure("readRLEByte", readRLEByte, fileName);
    measure("readRLEBuffer", readRLEBuffer, fileName);
#ifndef Windows
    measure("readRLEMmap", readRLEMmap, fileName);
#endif
    return 0;
}