-- LJSpeech 1.1 audio table.
-- Reads every .wav from the LJSpeech tarball, decodes the bytes as audio,
-- and surfaces the utterance id alongside the audio cell. The utt_id is
-- extracted from the filename stem ('LJSpeech-1.1/wavs/LJ001-0001.wav'
-- → 'LJ001-0001') so a sibling `transcripts` table can join on it.
SELECT
    -- 'LJSpeech-1.1/wavs/LJ001-0001.wav' → 'LJ001-0001'
    regexp_replace(path, '^' || $archive_stem || '/wavs/(.+)\.wav$', '\1') AS utt_id,
    audio_decode(bytes) AS clip,
    size AS file_bytes,
    modified
FROM open_archive($archive)
WHERE get_filename_ext(path) = 'wav'