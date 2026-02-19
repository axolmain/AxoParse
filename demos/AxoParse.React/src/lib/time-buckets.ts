export interface BucketResult {
    buckets: number[]
    min: number
    max: number
    bucketSize: number
}

export function bucketTimestamps(
    timestamps: number[],
    bucketCount: number,
    domain?: [number, number],
): BucketResult {
    if (timestamps.length === 0) {
        return {buckets: new Array(bucketCount).fill(0), min: 0, max: 0, bucketSize: 0}
    }

    let min: number
    let max: number

    if (domain) {
        min = domain[0]
        max = domain[1]
    } else {
        min = Infinity
        max = -Infinity
        for (let i = 0; i < timestamps.length; i++) {
            const ts = timestamps[i]
            if (ts < min) min = ts
            if (ts > max) max = ts
        }
    }

    const range = max - min || 1
    const bucketSize = range / bucketCount
    const buckets = new Array(bucketCount).fill(0)

    for (let i = 0; i < timestamps.length; i++) {
        const ts = timestamps[i]
        if (ts < min || ts > max) continue
        const idx = Math.min(Math.floor((ts - min) / bucketSize), bucketCount - 1)
        buckets[idx]++
    }

    return {buckets, min, max, bucketSize}
}
