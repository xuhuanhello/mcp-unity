import * as z from 'zod';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { Logger } from '../utils/logger.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';

const toolName = 'capture_screenshot';
const toolDescription = `Captures what the Unity Game view shows and returns it as an image, so you can see the result of your changes.
- source "game_view" (default): in Play mode, the real composited frame (all cameras + all UI). In Edit mode, while paused, or if no frame renders within 2s, it renders all cameras + Screen Space - Overlay canvases directly instead (UI Toolkit panels not included); "method" and "note" in the result say which.
- source "camera": one camera only (target = camera GameObject name or path; default Main Camera). Overlay UI not included.
- source "ui": UI only on a black background (target = a Canvas or any UI object under it; default all Screen Space - Overlay canvases).
Every capture is saved as a full-resolution PNG under <project>/.unity-mcp/screenshots/ (kept as review evidence, never overwritten); the result text gives its absolute path.
The attached image is only a downscaled PREVIEW (maxResolution, default 1024), good for checking overall layout and composition. Never judge fine detail (small text, 1px borders, alignment, exact colors) from the preview. Image viewers also downscale very large images, so even the full-resolution PNG is not seen pixel-for-pixel; for true 1:1 inspection use region:
1. Take a preview and find the area to inspect; the result states the preview scale (1 preview px = N full-resolution px).
2. Call again with region = that area in full-resolution pixels (origin top-left), ideally no larger than about 1500x1500. The crop is attached at 1:1 and saved next to the full capture.
Use includeImage=false to get only the saved file paths (no image) and read the PNG yourself.`;

const paramsSchema = z.object({
  source: z
    .enum(['game_view', 'camera', 'ui'])
    .optional()
    .describe('What to capture: "game_view" (default), "camera" or "ui"'),
  target: z
    .string()
    .optional()
    .describe('For "camera": camera GameObject name or path. For "ui": Canvas or UI object name or path. Ignored for "game_view"'),
  maxResolution: z
    .number()
    .int()
    .min(0)
    .optional()
    .describe('Longest edge in pixels of the attached image (default 1024 for a full capture; with region the crop is attached unscaled by default; 0 = never downscale). Saved PNGs are always full resolution'),
  includeImage: z
    .boolean()
    .optional()
    .describe('Attach an image (default true). Set false to get only the saved file paths and read the full-resolution PNG yourself'),
  region: z
    .object({
      x: z.number().int().min(0).describe('Left edge in full-resolution pixels'),
      y: z.number().int().min(0).describe('Top edge in full-resolution pixels (origin is top-left)'),
      width: z.number().int().min(1).describe('Width in full-resolution pixels'),
      height: z.number().int().min(1).describe('Height in full-resolution pixels')
    })
    .optional()
    .describe('Crop to this rectangle of the full-resolution capture to inspect it at 1:1 (keep it within about 1500x1500). Both the full capture and the crop are saved; clamped to the capture bounds'),
  fileName: z
    .string()
    .optional()
    .describe('File name for the saved PNG (default: timestamped). Saved under .unity-mcp/screenshots/; an existing file is never overwritten (a _1, _2 suffix is added)')
});

export function registerCaptureScreenshotTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to capture screenshot'
    );
  }

  const content: CallToolResult['content'] = [{
    type: 'text',
    text: response.message
  }];

  if (response.image?.data) {
    content.push({
      type: 'image',
      data: response.image.data,
      mimeType: response.image.mimeType || 'image/jpeg'
    });
  }

  return {
    content,
    data: {
      path: response.path,
      fullPath: response.fullPath,
      width: response.width,
      height: response.height,
      method: response.method,
      note: response.note,
      region: response.region,
      regionPath: response.regionPath,
      regionFullPath: response.regionFullPath
    }
  };
}
