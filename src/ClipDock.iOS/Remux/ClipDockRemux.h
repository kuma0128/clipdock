#pragma once

// Copy local media packets into an MP4. A second input supplies the audio track.
int CDRemux(const char *first, const char *audio, const char *output, int (^cancelled)(void));
