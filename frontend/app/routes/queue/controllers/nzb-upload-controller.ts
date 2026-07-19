import { useEffect } from "react";
import type { UploadingFile } from "../route";
import { withUrlBase } from "~/utils/url-base";
import {
    fallbackHttpFailure,
    parseBoundedJsonObject,
    renderPublicFailure,
    resolvePublicFailureEnvelope,
} from "~/utils/public-failure";

const MaximumUploadResponseBytes = 512;

export function initializeUploadController(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    uploadingFiles: UploadingFile[],
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
) {
    useEffect(() => {
        processUploadQueue(isUploadingRef, uploadQueueRef, setUploadingFiles);
    }, [uploadingFiles]);
}

async function processUploadQueue(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void
) {
    if (isUploadingRef.current || uploadQueueRef.current.length === 0) return;

    isUploadingRef.current = true;
    const fileToUpload = uploadQueueRef.current[0];

    setUploadingFiles(files => files.map(f =>
        f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
            ? { ...f, queueSlot: { ...f.queueSlot, status: 'uploading' } }
            : f
    ));

    try {
        const xhr = new XMLHttpRequest();
        const formData = new FormData();
        formData.append('nzbFile', fileToUpload.file, fileToUpload.file.name);

        xhr.responseType = 'text';
        xhr.upload.addEventListener('progress', (e) => {
            if (e.lengthComputable) {
                const progress = Math.round((e.loaded / e.total) * 100);
                setUploadingFiles(files => files.map(f =>
                    f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
                        ? {
                            ...f,
                            queueSlot: {
                                ...f.queueSlot,
                                percentage: progress.toString(),
                                true_percentage: progress.toString()
                            }
                        }
                        : f
                ));
            }
        });
        xhr.addEventListener('progress', (e) => {
            if (shouldAbortUploadResponse(e.loaded)) xhr.abort();
        });

        const responseText = await new Promise<string>((resolve, reject) => {
            xhr.addEventListener('load', () => {
                if (xhr.status >= 200 && xhr.status < 300) {
                    resolve(xhr.responseText);
                } else {
                    reject(new SafeUploadError(getUploadFailureMessage(xhr.status, xhr.responseText, name => xhr.getResponseHeader(name))));
                }
            });
            xhr.addEventListener('error', () => reject(new SafeUploadError('Upload failed.')));
            xhr.addEventListener('abort', () => reject(new SafeUploadError('Upload failed.')));

            xhr.open('POST', withUrlBase(`/api?mode=addfile&cat=${fileToUpload.queueSlot.cat}&priority=0&pp=0`));
            xhr.send(formData);
        });

        const response = parseBoundedJsonObject(responseText);
        if (response?.status !== true) {
            throw new SafeUploadError(getUploadFailureMessage(
                xhr.status,
                responseText,
                name => xhr.getResponseHeader(name)));
        }

    } catch (error) {
        setUploadingFiles(files => files.map(f =>
            f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id ? {
                ...f,
                queueSlot: {
                    ...f.queueSlot,
                    status: 'upload failed',
                    error: error instanceof SafeUploadError ? error.message : 'Upload failed.'
                }
            } : f
        ));
    }

    uploadQueueRef.current = uploadQueueRef.current.filter(x => x !== fileToUpload);
    isUploadingRef.current = false;

    if (uploadQueueRef.current.length > 0) {
        processUploadQueue(isUploadingRef, uploadQueueRef, setUploadingFiles);
    }
}

class SafeUploadError extends Error {}

export function getUploadFailureMessage(
    status: number,
    body: string,
    getHeader: (name: string) => string | null,
): string {
    const headers = { get: getHeader };
    const envelope = resolvePublicFailureEnvelope(body, headers);
    return envelope ? renderPublicFailure(envelope) : fallbackHttpFailure(status);
}

export function shouldAbortUploadResponse(loadedBytes: number): boolean {
    return loadedBytes > MaximumUploadResponseBytes;
}
