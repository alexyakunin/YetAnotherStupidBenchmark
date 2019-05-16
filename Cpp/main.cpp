#include <cstdio>
#include <cstdint>
#include <fcntl.h>
#include <unistd.h>

auto readRLE() {
    auto fIn = open("C:\\Downloads\\26_dump.zip", O_RDONLY | O_BINARY, 0644);

    static constexpr size_t BUFFER_SIZE = 1 << 16;
    uint8_t buffer[BUFFER_SIZE];
    size_t bufferPos = 0;
    size_t bufferLen = 0;
    uint64_t totalSize = 0;

    auto readByte = [&](uint8_t& result) {
        if (bufferLen == bufferPos) {
            bufferLen = read(fIn, buffer, BUFFER_SIZE);
            totalSize += bufferLen;
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
            if (!readByte(b))
                return sum + n;
        }
        n = (n << 7) + b - 128;
        sum += n;
    }
    close(fIn);
    return sum;
}

int main() {
    auto sum = readRLE();
    printf("Sum: %lld\n", sum);
    return 0;
}