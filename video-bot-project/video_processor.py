import logging
import os
import random
import subprocess
import math

from config import Config

logger = logging.getLogger(__name__)


class VideoProcessor:
    """FFmpeg-based video processing with 9 transformation functions."""

    OPTION_LABELS = {
        "mirror": "🪞 Зеркало",
        "reduce_audio": "🔉 Звук −10-20%",
        "slow_down": "🐌 Замедлить 4-7%",
        "speed_up": "⚡ Ускорить 3-5%",
        "color_correct": "🎨 Цветокоррекция",
        "slice": "✂️ Нарезка 2:30–3:10",
        "slice_long": "✂️ Нарезка 5:10–7:10",
        "qr_overlay": "📱 QR-код",
        "rotate": "🔄 Поворот 3-5°",
        "downscale_1080p": "📐 Даунскейл 1080p",
    }

    OPTION_IDS = list(OPTION_LABELS.keys())

    def __init__(self):
        Config.ensure_dirs()
        self.qr_path = os.path.join(os.path.dirname(__file__), Config.QR_OVERLAY_PATH)

    @staticmethod
    def _run_ffmpeg(args: list[str], desc: str = ""):
        """Run an FFmpeg command and handle errors."""
        cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "warning"] + args
        logger.info(f"FFmpeg [{desc}]: {' '.join(cmd)}")
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            logger.error(f"FFmpeg error [{desc}]: {result.stderr}")
            raise RuntimeError(f"FFmpeg failed ({desc}): {result.stderr[:500]}")
        return result

    @staticmethod
    def _run_ffmpeg_with_progress(args: list[str], desc: str = "",
                                   duration_sec: float = 0,
                                   progress_cb=None):
        """Run FFmpeg with progress reporting via callback."""
        cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "warning",
               "-progress", "pipe:1"] + args
        logger.info(f"FFmpeg [{desc}]: {' '.join(cmd)}")
        proc = subprocess.Popen(
            cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        last_pct = -1
        try:
            for line in proc.stdout:
                line = line.strip()
                if line.startswith("out_time_us=") and duration_sec > 0 and progress_cb:
                    try:
                        us = int(line.split("=", 1)[1])
                        current_sec = us / 1_000_000
                        pct = min(int(current_sec / duration_sec * 100), 99)
                        if pct >= last_pct + 15:  # Update every 15%
                            last_pct = pct
                            progress_cb(pct)
                    except (ValueError, ZeroDivisionError):
                        pass
        except Exception:
            pass
        proc.wait()
        if proc.returncode != 0:
            stderr = proc.stderr.read() if proc.stderr else ""
            logger.error(f"FFmpeg error [{desc}]: {stderr}")
            raise RuntimeError(f"FFmpeg failed ({desc}): {stderr[:500]}")
        if progress_cb:
            progress_cb(100)

    @staticmethod
    def _get_duration(input_path: str) -> float:
        """Get video duration in seconds using ffprobe."""
        cmd = [
            "ffprobe", "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            input_path,
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"ffprobe failed: {result.stderr[:500]}")
        import json
        info = json.loads(result.stdout)
        return float(info["format"]["duration"])

    @staticmethod
    def _make_output_path(input_path: str, suffix: str) -> str:
        """Generate output path in the processed directory with a suffix."""
        base = os.path.splitext(os.path.basename(input_path))[0]
        ext = os.path.splitext(input_path)[1]
        output = os.path.join(Config.PROCESSED_DIR, f"{base}_{suffix}{ext}")
        return output

    # ── 1. Mirror ──────────────────────────────────────────────

    def mirror(self, input_path: str) -> str:
        output = self._make_output_path(input_path, "mirror")
        self._run_ffmpeg([
            "-i", input_path,
            "-vf", "hflip",
            "-c:a", "copy",
            output,
        ], "mirror")
        return output

    # ── 2. Reduce Audio ────────────────────────────────────────

    def reduce_audio(self, input_path: str) -> str:
        reduction = random.uniform(0.80, 0.90)
        suffix = f"audio{int(reduction * 100)}"
        output = self._make_output_path(input_path, suffix)
        self._run_ffmpeg([
            "-i", input_path,
            "-af", f"volume={reduction:.2f}",
            "-c:v", "copy",
            output,
        ], f"reduce_audio to {reduction:.0%}")
        return output

    # ── 3. Slow Down ───────────────────────────────────────────

    def slow_down(self, input_path: str) -> str:
        factor = random.uniform(1.04, 1.07)
        suffix = f"slow{int(factor * 100)}"
        output = self._make_output_path(input_path, suffix)
        # Video: setpts multiplies timestamps; Audio: atempo is inverse
        atempo = 1.0 / factor
        self._run_ffmpeg([
            "-i", input_path,
            "-filter_complex",
            f"[0:v]setpts={factor}*PTS[v];[0:a]atempo={atempo:.4f}[a]",
            "-map", "[v]", "-map", "[a]",
            output,
        ], f"slow_down {factor:.2f}x")
        return output

    # ── 4. Speed Up ────────────────────────────────────────────

    def speed_up(self, input_path: str) -> str:
        # Speed up by 3-5% means factor 1.03-1.05 for playback speed
        speed_factor = random.uniform(1.03, 1.05)
        pts_factor = 1.0 / speed_factor
        suffix = f"fast{int(speed_factor * 100)}"
        output = self._make_output_path(input_path, suffix)
        self._run_ffmpeg([
            "-i", input_path,
            "-filter_complex",
            f"[0:v]setpts={pts_factor:.4f}*PTS[v];[0:a]atempo={speed_factor:.4f}[a]",
            "-map", "[v]", "-map", "[a]",
            output,
        ], f"speed_up {speed_factor:.2f}x")
        return output

    # ── 5. Color Correction ────────────────────────────────────

    def color_correct(self, input_path: str) -> str:
        saturation = 1.0 + random.uniform(0.04, 0.07)   # +4-7%
        brightness = -random.uniform(0.04, 0.07)          # -4-7%
        gamma = 1.0 - random.uniform(0.01, 0.03)          # exposure -1-3%
        suffix = "color"
        output = self._make_output_path(input_path, suffix)
        self._run_ffmpeg([
            "-i", input_path,
            "-vf", f"eq=saturation={saturation:.3f}:brightness={brightness:.3f}:gamma={gamma:.3f}",
            "-c:a", "copy",
            output,
        ], f"color sat={saturation:.2f} bright={brightness:.2f} gamma={gamma:.2f}")
        return output

    # ── 6. Slice Video ─────────────────────────────────────────

    def slice_video(self, input_path: str, min_dur: float = 150, max_dur: float = 190, min_leftover: float = 30) -> list[str]:
        """Slice video into segments with random duration."""
        duration = self._get_duration(input_path)

        base = os.path.splitext(os.path.basename(input_path))[0]
        ext = os.path.splitext(input_path)[1]
        outputs = []

        start = 0.0
        part_num = 1
        while start < duration:
            slice_dur = random.uniform(min_dur, max_dur)
            remaining = duration - start
            if remaining < min_leftover:
                break
            slice_dur = min(slice_dur, remaining)

            output = os.path.join(Config.PROCESSED_DIR, f"{base}_part{part_num:02d}{ext}")
            dur_str = f"{int(slice_dur // 60)}:{int(slice_dur % 60):02d}"
            self._run_ffmpeg([
                "-i", input_path,
                "-ss", str(start),
                "-t", str(slice_dur),
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                output,
            ], f"slice part {part_num} (start={int(start)}s, dur={dur_str})")
            outputs.append(output)

            start += slice_dur
            part_num += 1

        logger.info(f"Sliced into {len(outputs)} parts")
        return outputs

    # ── 7. QR Code Overlay ─────────────────────────────────────

    def qr_overlay(self, input_path: str) -> str:
        if not os.path.exists(self.qr_path):
            raise FileNotFoundError(
                f"QR overlay image not found: {self.qr_path}"
            )
        output = self._make_output_path(input_path, "qr")
        # Scale QR to 25% of video width, place in bottom-right, show first 10s only
        self._run_ffmpeg([
            "-i", input_path,
            "-i", self.qr_path,
            "-filter_complex",
            "[1:v]scale=iw*0.25:-1[qr];[0:v][qr]overlay=W-w-20:H-h-20:enable='between(t,0,10)'",
            "-c:a", "copy",
            output,
        ], "qr_overlay")
        return output

    # ── 8. Rotate ──────────────────────────────────────────────

    def rotate(self, input_path: str) -> str:
        degrees = random.uniform(3.0, 5.0)
        radians = degrees * math.pi / 180
        suffix = f"rot{int(degrees)}"
        output = self._make_output_path(input_path, suffix)
        # fillcolor=black fills the corners exposed by rotation
        self._run_ffmpeg([
            "-i", input_path,
            "-vf", f"rotate={radians:.4f}:fillcolor=black",
            "-c:a", "copy",
            output,
        ], f"rotate {degrees:.1f}°")
        return output

    # ── Combined Process Pipeline ──────────────────────────────

    def process(self, input_path: str, selected_options: list[str]) -> list[str]:
        """
        Process a video with ALL selected options combined into one output.
        
        If 'slice' or 'slice_long' is selected, the video is first sliced,
        then all other filters are applied to each segment.
        
        Returns list of output file paths.
        """
        slice_opts = {"slice", "slice_long"}
        do_slice = "slice" in selected_options
        do_slice_long = "slice_long" in selected_options
        filter_options = [opt for opt in selected_options if opt not in slice_opts]

        if do_slice or do_slice_long:
            if do_slice_long:
                sliced_parts = self.slice_video(input_path, min_dur=310, max_dur=430, min_leftover=60)
            else:
                sliced_parts = self.slice_video(input_path)

            if not filter_options:
                return sliced_parts

            combined_outputs = []
            for part_path in sliced_parts:
                output = self._apply_combined(part_path, filter_options)
                combined_outputs.append(output)
            return combined_outputs
        else:
            if not filter_options:
                return []
            output = self._apply_combined(input_path, filter_options)
            return [output]

    def _apply_combined(self, input_path: str, options: list[str], progress_cb=None) -> str:
        """Apply ALL selected filters combined into a single FFmpeg command."""
        # Collect filter components
        vfilters = []       # video filters to chain
        afilters = []       # audio filters to chain
        has_qr = "qr_overlay" in options
        speed_pts = None    # setpts factor for video
        atempo_val = None   # atempo factor for audio
        suffix_parts = []
        extra_inputs = []
        desc_parts = []

        for opt in options:
            if opt == "mirror":
                vfilters.append("hflip")
                suffix_parts.append("mir")
                desc_parts.append("mirror")

            elif opt == "reduce_audio":
                vol = random.uniform(0.80, 0.90)
                afilters.append(f"volume={vol:.2f}")
                suffix_parts.append(f"vol{int(vol * 100)}")
                desc_parts.append(f"audio {vol:.0%}")

            elif opt == "slow_down":
                factor = random.uniform(1.04, 1.07)
                speed_pts = factor
                atempo_val = 1.0 / factor
                suffix_parts.append(f"slow{int(factor * 100)}")
                desc_parts.append(f"Замедление {factor:.2f}x")

            elif opt == "speed_up":
                factor = random.uniform(1.03, 1.05)
                speed_pts = 1.0 / factor
                atempo_val = factor
                suffix_parts.append(f"fast{int(factor * 100)}")
                desc_parts.append(f"Ускорение {factor:.2f}x")

            elif opt == "color_correct":
                sat = 1.0 + random.uniform(0.04, 0.07)
                bright = -random.uniform(0.04, 0.07)
                gamma = 1.0 - random.uniform(0.01, 0.03)
                vfilters.append(f"eq=saturation={sat:.3f}:brightness={bright:.3f}:gamma={gamma:.3f}")
                suffix_parts.append("color")
                sat_pct = (sat - 1.0) * 100
                bright_pct = bright * 100
                gamma_pct = (1.0 - gamma) * 100
                desc_parts.append(
                    f"Цвет: насыщ.+{sat_pct:.0f}% ярк.{bright_pct:.0f}% эксп.{-gamma_pct:.0f}%"
                )

            elif opt == "qr_overlay":
                if not os.path.exists(self.qr_path):
                    raise FileNotFoundError(
                        f"QR overlay image not found: {self.qr_path}"
                    )
                suffix_parts.append("qr")
                desc_parts.append("qr")

            elif opt == "rotate":
                degrees = random.uniform(3.0, 5.0)
                radians = degrees * math.pi / 180
                zoom = 1.0 + random.uniform(0.10, 0.15)  # 10-15% zoom to hide black bars
                vfilters.append(f"scale=iw*{zoom:.2f}:ih*{zoom:.2f}")
                vfilters.append(f"rotate={radians:.4f}:fillcolor=black")
                vfilters.append(f"crop=iw/{zoom:.2f}:ih/{zoom:.2f}")
                suffix_parts.append(f"rot{int(degrees)}")
                desc_parts.append(f"Поворот {degrees:.1f}° зум {zoom:.0%}")

            elif opt == "downscale_1080p":
                vfilters.append("scale=-2:1080")
                suffix_parts.append("1080p")
                desc_parts.append("Даунскейл 1080p")

        # Build the suffix for output filename
        suffix = "_".join(suffix_parts) if suffix_parts else "processed"
        output = self._make_output_path(input_path, suffix)

        # Add speed filter (setpts) to video filters
        if speed_pts is not None:
            vfilters.insert(0, f"setpts={speed_pts:.4f}*PTS")
        if atempo_val is not None:
            afilters.insert(0, f"atempo={atempo_val:.4f}")

        # Build FFmpeg command
        cmd = ["-i", input_path]

        if has_qr:
            cmd += ["-i", self.qr_path]

        # Build filter_complex
        filter_parts = []

        # Video chain
        v_label_in = "0:v"
        if vfilters or has_qr:
            if vfilters:
                vf_chain = ",".join(vfilters)
                filter_parts.append(f"[{v_label_in}]{vf_chain}[vprocessed]")
                v_label_in = "vprocessed"

            if has_qr:
                # Scale QR and overlay — show only first 10 seconds
                filter_parts.append(f"[1:v]scale=iw*0.25:-1[qr]")
                filter_parts.append(f"[{v_label_in}][qr]overlay=W-w-20:H-h-20:enable='between(t,0,10)'[vout]")
                v_label_in = "vout"
            else:
                # Rename final video label
                if vfilters:
                    # Already labeled as vprocessed, rename to vout
                    pass  # we'll use vprocessed

        # Audio chain
        a_label_in = "0:a"
        if afilters:
            af_chain = ",".join(afilters)
            filter_parts.append(f"[{a_label_in}]{af_chain}[aout]")
            a_label_in = "aout"

        # Determine final labels
        final_v = v_label_in if v_label_in != "0:v" else None
        final_a = a_label_in if a_label_in != "0:a" else None

        if filter_parts:
            cmd += ["-filter_complex", ";".join(filter_parts)]
            if final_v:
                cmd += ["-map", f"[{final_v}]"]
            else:
                cmd += ["-map", "0:v"]
            if final_a:
                cmd += ["-map", f"[{final_a}]"]
            else:
                cmd += ["-map", "0:a"]

            # Encoding quality settings
            if final_v:
                bitrate = "10000k" if "downscale_1080p" in options else "35000k"
                cmd += ["-c:v", "libx264", "-preset", "ultrafast",
                        "-b:v", bitrate, "-threads", "0"]
            else:
                cmd += ["-c:v", "copy"]

            if final_a:
                cmd += ["-c:a", "aac", "-b:a", "192k"]
            else:
                cmd += ["-c:a", "copy"]
        else:
            # No filters at all — just copy
            cmd += ["-c", "copy"]

        cmd.append(output)

        desc = " | ".join(desc_parts)
        duration_sec = self._get_duration(input_path)
        self._run_ffmpeg_with_progress(
            cmd, f"combined: {desc}",
            duration_sec=duration_sec,
            progress_cb=progress_cb,
        )
        return output, desc

