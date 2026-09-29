/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

/*
 * Clock skew shim for the clock-skew fault. Preloaded (LD_PRELOAD) into a node's process, it shifts
 * that process's wall clock — CLOCK_REALTIME and its variants, gettimeofday and time — by an offset
 * read from a file. Monotonic clocks, sleeps and timers are left untouched, so the node's timeouts,
 * Raft election timers and leader leases keep real durations while every timestamp it reads from the
 * wall clock (the HLC's physical component, lock and intent expiries, session expiries) is skewed.
 *
 * libfaketime is not used because it hooks sleeps and condition-variable waits too: under .NET 10 it
 * made Thread.Sleep and timed waits return immediately, even with FAKETIME_DONT_FAKE_MONOTONIC=1 and
 * with FAKE_SLEEP/FAKE_TIMERS compiled out, which breaks every timer in the process.
 *
 * CARAXES_SKEW_FILE (default /tmp/caraxes-skew) holds one signed integer: the offset in milliseconds.
 * A missing or unparsable file means an offset of zero. The file is re-read at most once every
 * CARAXES_SKEW_REFRESH_MS (default 100) of monotonic time, so a change reaches the process within
 * that interval without a restart. The writer replaces the file with a rename, so a reader never sees
 * a partial write. The file is read with open/read/close, which are async-signal-safe, because the
 * runtime may read the clock from a signal handler.
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#define NS_PER_MS 1000000LL
#define NS_PER_S 1000000000LL

typedef int (*clock_gettime_fn)(clockid_t, struct timespec *);

static clock_gettime_fn next_clock_gettime;
static const char *skew_path = "/tmp/caraxes-skew";
static long long refresh_ns = 100 * NS_PER_MS;
static _Atomic long long offset_ns;
static _Atomic long long next_refresh_ns;

static int raw_clock_gettime(clockid_t id, struct timespec *ts)
{
    if (next_clock_gettime != NULL)
        return next_clock_gettime(id, ts);
    return (int)syscall(SYS_clock_gettime, id, ts);
}

__attribute__((constructor)) static void skew_init(void)
{
    next_clock_gettime = (clock_gettime_fn)dlsym(RTLD_NEXT, "clock_gettime");

    const char *path = getenv("CARAXES_SKEW_FILE");
    if (path != NULL && path[0] != '\0')
        skew_path = path;

    const char *refresh = getenv("CARAXES_SKEW_REFRESH_MS");
    if (refresh != NULL && atoll(refresh) > 0)
        refresh_ns = atoll(refresh) * NS_PER_MS;
}

/* Parses an optional sign and decimal digits; anything else ends the number. */
static long long parse_ms(const char *buf, ssize_t len)
{
    long long value = 0;
    int negative = 0;
    ssize_t i = 0;

    while (i < len && (buf[i] == ' ' || buf[i] == '\t'))
        i++;
    if (i < len && (buf[i] == '-' || buf[i] == '+'))
        negative = buf[i++] == '-';
    for (; i < len && buf[i] >= '0' && buf[i] <= '9'; i++)
        value = value * 10 + (buf[i] - '0');

    return negative ? -value : value;
}

static long long read_offset_ns(void)
{
    int fd = open(skew_path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
        return 0;

    char buf[32];
    ssize_t len = read(fd, buf, sizeof(buf));
    close(fd);

    return len > 0 ? parse_ms(buf, len) * NS_PER_MS : 0;
}

static long long current_offset_ns(void)
{
    struct timespec mono;
    if (raw_clock_gettime(CLOCK_MONOTONIC_COARSE, &mono) != 0)
        return atomic_load_explicit(&offset_ns, memory_order_relaxed);

    long long now = mono.tv_sec * NS_PER_S + mono.tv_nsec;
    long long due = atomic_load_explicit(&next_refresh_ns, memory_order_relaxed);

    /* One thread wins the refresh; the others keep using the last offset. */
    if (now >= due && atomic_compare_exchange_strong(&next_refresh_ns, &due, now + refresh_ns))
        atomic_store_explicit(&offset_ns, read_offset_ns(), memory_order_relaxed);

    return atomic_load_explicit(&offset_ns, memory_order_relaxed);
}

static int is_wall_clock(clockid_t id)
{
    return id == CLOCK_REALTIME || id == CLOCK_REALTIME_COARSE || id == CLOCK_TAI;
}

static void shift(struct timespec *ts, long long by)
{
    long long ns = ts->tv_sec * NS_PER_S + ts->tv_nsec + by;
    ts->tv_sec = ns / NS_PER_S;
    ts->tv_nsec = ns % NS_PER_S;
    if (ts->tv_nsec < 0)
    {
        ts->tv_nsec += NS_PER_S;
        ts->tv_sec -= 1;
    }
}

int clock_gettime(clockid_t id, struct timespec *ts)
{
    int rc = raw_clock_gettime(id, ts);
    if (rc == 0 && is_wall_clock(id))
        shift(ts, current_offset_ns());
    return rc;
}

int gettimeofday(struct timeval *restrict tv, void *restrict tz)
{
    (void)tz;
    struct timespec ts;
    int rc = clock_gettime(CLOCK_REALTIME, &ts);
    if (rc == 0 && tv != NULL)
    {
        tv->tv_sec = ts.tv_sec;
        tv->tv_usec = ts.tv_nsec / 1000;
    }
    return rc;
}

time_t time(time_t *out)
{
    struct timespec ts;
    if (clock_gettime(CLOCK_REALTIME, &ts) != 0)
        return (time_t)-1;
    if (out != NULL)
        *out = ts.tv_sec;
    return ts.tv_sec;
}
