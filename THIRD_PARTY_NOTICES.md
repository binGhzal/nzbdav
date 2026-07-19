# Third-party notices

## React Router entry templates

`frontend/app/entry.server.tsx` and `frontend/app/entry.client.tsx` are adapted
from the default Node server and client entry templates distributed in the
installed `@react-router/dev` 7.18.1 package. The repository declares compatible
React Router caret ranges beginning at 7.5.3 and 7.16.0, and the lockfile
currently resolves the installed package to 7.18.1. The templates and React
Router are distributed under the following MIT license:

MIT License

Copyright (c) React Training LLC 2015-2019
Copyright (c) Remix Software Inc. 2020-2021
Copyright (c) Shopify Inc. 2022-2023

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The thin Express request/response bridge in
`frontend/server/react-router-handler.ts` is also adapted from the
`@react-router/express` 7.18.1 server adapter under the same MIT license above.
