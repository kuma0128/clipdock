#include "ClipDockRemux.h"
#include <libavformat/avformat.h>
#include <libavutil/mathematics.h>

typedef struct {
    AVFormatContext *format;
    AVPacket *packet;
    int video, audio;
    int out_video, out_audio;
    int wrote_video, wrote_audio;
} Input;

static int add_stream(Input *in, AVFormatContext *output, enum AVMediaType type, int *index, int *mapped) {
    *index = av_find_best_stream(in->format, type, -1, -1, NULL, 0);
    if (*index == AVERROR_STREAM_NOT_FOUND) return 0;
    if (*index < 0) return *index;
    AVStream *source = in->format->streams[*index];
    AVStream *dest = avformat_new_stream(output, NULL);
    if (!dest) return AVERROR(ENOMEM);
    int ret = avcodec_parameters_copy(dest->codecpar, source->codecpar);
    if (ret < 0) return ret;
    dest->codecpar->codec_tag = 0;
    dest->time_base = source->time_base;
    dest->avg_frame_rate = source->avg_frame_rate;
    *mapped = dest->index;
    return 0;
}

static int open_input(Input *in, const char *path, AVFormatContext *output, int video, int audio) {
    in->video = in->audio = -1;
    int ret = avformat_open_input(&in->format, path, NULL, NULL);
    if (ret < 0) return ret;
    if ((ret = avformat_find_stream_info(in->format, NULL)) < 0) return ret;
    if (video && (ret = add_stream(in, output, AVMEDIA_TYPE_VIDEO, &in->video, &in->out_video)) < 0) return ret;
    if (audio && (ret = add_stream(in, output, AVMEDIA_TYPE_AUDIO, &in->audio, &in->out_audio)) < 0) return ret;
    if (in->video < 0 && in->audio < 0) return AVERROR_STREAM_NOT_FOUND;
    in->packet = av_packet_alloc();
    return in->packet ? 0 : AVERROR(ENOMEM);
}

// Keep one pending packet per input so even long, separate tracks are interleaved in bounded memory.
static int read_packet(Input *in, int (^cancelled)(void)) {
    for (;;) {
        if (cancelled()) return AVERROR_EXIT;
        int ret = av_read_frame(in->format, in->packet);
        if (ret == AVERROR_EOF) {
            av_packet_free(&in->packet);
            return 0;
        }
        if (ret < 0) return ret;
        AVPacket *p = in->packet;
        if (p->stream_index == in->video || p->stream_index == in->audio) {
            p->time_base = in->format->streams[p->stream_index]->time_base;
            if (in->format->start_time != AV_NOPTS_VALUE) {
                int64_t start = av_rescale_q(in->format->start_time, AV_TIME_BASE_Q, p->time_base);
                if (p->pts != AV_NOPTS_VALUE) p->pts -= start;
                if (p->dts != AV_NOPTS_VALUE) p->dts -= start;
            }
            return 0;
        }
        av_packet_unref(p);
    }
}

int CDRemux(const char *first, const char *audio, const char *path, int (^cancelled)(void)) {
    Input inputs[2] = {0};
    int count = audio ? 2 : 1;
    AVFormatContext *output = NULL;
    int ret = avformat_alloc_output_context2(&output, NULL, "mp4", path);
    if (ret < 0) return ret;
    if ((ret = open_input(&inputs[0], first, output, 1, !audio)) < 0) goto done;
    if (audio && (ret = open_input(&inputs[1], audio, output, 0, 1)) < 0) goto done;
    if ((ret = avio_open(&output->pb, path, AVIO_FLAG_WRITE)) < 0) goto done;
    if ((ret = avformat_write_header(output, NULL)) < 0) goto done;
    for (int i = 0; i < count; ++i) {
        if ((ret = read_packet(&inputs[i], cancelled)) < 0) goto done;
    }

    while (inputs[0].packet || inputs[1].packet) {
        Input *in = &inputs[0];
        AVPacket *a = inputs[0].packet, *b = inputs[1].packet;
        if (!a || (b && av_compare_ts(b->dts, b->time_base, a->dts, a->time_base) < 0)) in = &inputs[1];
        AVPacket *p = in->packet;
        if (p->stream_index == in->video) {
            in->wrote_video = 1;
            p->stream_index = in->out_video;
        } else {
            in->wrote_audio = 1;
            p->stream_index = in->out_audio;
        }
        av_packet_rescale_ts(p, p->time_base, output->streams[p->stream_index]->time_base);
        p->pos = -1;
        if ((ret = av_interleaved_write_frame(output, p)) < 0) goto done;
        if ((ret = read_packet(in, cancelled)) < 0) goto done;
    }
    for (int i = 0; i < count; ++i) {
        if ((inputs[i].video >= 0 && !inputs[i].wrote_video) || (inputs[i].audio >= 0 && !inputs[i].wrote_audio)) {
            ret = AVERROR_INVALIDDATA;
            goto done;
        }
    }
    ret = av_write_trailer(output);

done:
    for (int i = 0; i < count; ++i) {
        av_packet_free(&inputs[i].packet);
        avformat_close_input(&inputs[i].format);
    }
    if (output->pb) {
        int close_result = avio_closep(&output->pb);
        if (ret >= 0) ret = close_result;
    }
    avformat_free_context(output);
    return ret;
}
