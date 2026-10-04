Create a local CSV dashboard using index.html, styles.css and app.js. Use no CDN,
external packages or network requests. A user uploads a CSV with columns
name,category,amount. Render its rows, filter by category, sort by numeric amount,
show the sum of visible amounts, draw a bar chart for visible rows, and export the
currently visible rows as CSV. Show a clear message for empty or invalid CSV.

Use these stable, accessible controls so the result can be checked through the UI:

- <input type="file" id="csv-file"> for upload.
- <select id="category-filter"> with "all" and the loaded categories as values.
- <button id="sort-amount"> toggles numeric ascending/descending order.
- <tbody id="rows"> with name, category and amount in that order.
- <span id="total"> shows the visible amount sum.
- <svg id="chart"> with one visible <rect> per visible row.
- <button id="export"> downloads the currently visible rows as CSV.
- <p id="status"> displays any empty/invalid CSV message.

Use the real workspace files and read them back. Run web.verify if available,
fix any errors it reports, and finish only after checking the result.
