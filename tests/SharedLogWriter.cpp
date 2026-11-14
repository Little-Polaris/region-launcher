#include "../src/SharedNetworkLog.h"
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 1;
    FILE* log = OpenNetworkLog(argv[1]);
    if (!log) return 2;
    for (int i = 0; i < 30000; ++i) fputs("fixture network line for bounded snapshot\n", log);
    fputs("held-writer token=fixture-secret\n", log); fflush(log);
    puts("READY"); fflush(stdout);
    getchar();
    fputs("writer-still-usable\n", log); fclose(log);
    return 0;
}
